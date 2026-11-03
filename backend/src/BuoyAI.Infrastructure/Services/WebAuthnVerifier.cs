using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The WebAuthn checks a passkey needs: read the credential's public key (a COSE key) out of the
/// registration's attestation object, and verify a sign-in assertion's signature with it.
///
/// Until this existed, registration stored a hash of the attestation object instead of the key, and
/// sign-in accepted any signature of 32 bytes or more, so anyone holding a passkey's credential id
/// could get a login token. Only the two algorithms platform passkeys use are accepted: ES256 (-7)
/// and RS256 (-257). The attestation statement itself is not checked: BuoyAI asks for no
/// attestation and trusts the key the authenticator reports, as most relying parties do.
/// </summary>
internal static class WebAuthnVerifier
{
    private const byte UserPresentFlag = 0x01;
    private const byte AttestedCredentialFlag = 0x40;
    private const int MinAuthDataLength = 37; // rpIdHash (32) + flags (1) + signCount (4)

    /// <summary>
    /// The credential's public key from a registration's attestation object, after checking that it
    /// was made for this relying party with the user present. Throws InvalidOperationException otherwise.
    /// </summary>
    public static byte[] ReadCredentialPublicKey(byte[] attestationObject, string rpId, bool requireUserVerified = false)
    {
        byte[]? authData = null;
        var reader = new CborReader(attestationObject, CborConformanceMode.Lax);
        reader.ReadStartMap();
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var name = reader.ReadTextString();
            if (name == "authData")
                authData = reader.ReadByteString();
            else
                reader.SkipValue();
        }
        reader.ReadEndMap();

        if (authData is null || !IsForRelyingPartyWithUserPresent(authData, rpId))
            throw new InvalidOperationException("The passkey was not made for this site");
        if (requireUserVerified && !IsUserVerified(authData))
            throw new InvalidOperationException("The passkey didn't check it was you (Face ID, fingerprint or PIN)");
        if ((authData[32] & AttestedCredentialFlag) == 0 || authData.Length < 55)
            throw new InvalidOperationException("The passkey response has no credential");

        // aaguid (16 bytes at 37), credential id length (2 bytes at 53), credential id, then the key.
        var keyStart = 55 + ((authData[53] << 8) | authData[54]);
        if (authData.Length <= keyStart)
            throw new InvalidOperationException("The passkey response has no public key");

        // The key is one CBOR item; extension data may follow it.
        var keyReader = new CborReader(authData.AsMemory(keyStart), CborConformanceMode.Lax, allowMultipleRootLevelValues: true);
        var coseKey = keyReader.ReadEncodedValue().ToArray();
        if (ReadCoseKey(coseKey) is null)
            throw new InvalidOperationException("This kind of passkey isn't supported");
        return coseKey;
    }

    /// <summary>True when the stored key is a COSE key this verifier can use.</summary>
    public static bool IsSupportedKey(byte[] storedKey) => ReadCoseKey(storedKey) is not null;

    /// <summary>
    /// Verifies a sign-in assertion: made for this relying party, with the user present, and signed
    /// over authenticatorData followed by SHA-256(clientDataJSON) by the stored key.
    /// </summary>
    public static bool VerifyAssertion(byte[] authData, byte[] clientDataJson, byte[] signature, byte[] storedKey, string rpId,
        bool requireUserVerified = false)
    {
        if (!IsForRelyingPartyWithUserPresent(authData, rpId))
            return false;
        if (requireUserVerified && !IsUserVerified(authData))
            return false;
        var key = ReadCoseKey(storedKey);
        if (key is null)
            return false;

        var signed = new byte[authData.Length + 32];
        authData.CopyTo(signed, 0);
        SHA256.HashData(clientDataJson).CopyTo(signed, authData.Length);

        try
        {
            if (key.Ec is { } ecParameters)
            {
                using var ecdsa = ECDsa.Create(ecParameters);
                return ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
            using var rsa = RSA.Create(key.Rsa!.Value);
            return rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The UV flag: the authenticator checked the person (Face ID, fingerprint, PIN), not just a tap.</summary>
    public static bool IsUserVerified(byte[] authData) =>
        authData.Length > 32 && (authData[32] & UserVerifiedFlag) != 0;

    private const byte UserVerifiedFlag = 0x04;

    private static bool IsForRelyingPartyWithUserPresent(byte[] authData, string rpId) =>
        authData.Length >= MinAuthDataLength
        && authData.AsSpan(0, 32).SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)))
        && (authData[32] & UserPresentFlag) != 0;

    private sealed record CoseKey(ECParameters? Ec, RSAParameters? Rsa);

    /// <summary>An ES256 (EC2, P-256) or RS256 (RSA) COSE key, or null for anything else.</summary>
    private static CoseKey? ReadCoseKey(byte[] cose)
    {
        try
        {
            var reader = new CborReader(cose, CborConformanceMode.Lax);
            reader.ReadStartMap();
            int? kty = null, alg = null, crv = null;
            byte[]? minus1 = null, minus2 = null, minus3 = null;
            while (reader.PeekState() != CborReaderState.EndMap)
            {
                if (reader.PeekState() is not (CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger))
                {
                    reader.SkipValue(); // a text label: not one of ours
                    reader.SkipValue();
                    continue;
                }
                switch (reader.ReadInt32())
                {
                    case 1: kty = reader.ReadInt32(); break;
                    case 3: alg = reader.ReadInt32(); break;
                    // -1 is the curve (an integer) for EC2 keys and the modulus (bytes) for RSA keys.
                    case -1 when reader.PeekState() == CborReaderState.ByteString: minus1 = reader.ReadByteString(); break;
                    case -1: crv = reader.ReadInt32(); break;
                    case -2: minus2 = reader.ReadByteString(); break;
                    case -3: minus3 = reader.ReadByteString(); break;
                    default: reader.SkipValue(); break;
                }
            }
            reader.ReadEndMap();

            if (kty == 2 && alg == -7 && crv == 1 && minus2 is { Length: 32 } && minus3 is { Length: 32 })
            {
                var ec = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = minus2, Y = minus3 } };
                using var _ = ECDsa.Create(ec); // throws when the point isn't on the curve
                return new CoseKey(ec, null);
            }
            if (kty == 3 && alg == -257 && minus1 is { Length: >= 256 } && minus2 is { Length: > 0 })
                return new CoseKey(null, new RSAParameters { Modulus = minus1, Exponent = minus2 });
            return null;
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException or CryptographicException or OverflowException)
        {
            return null;
        }
    }
}
