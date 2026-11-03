using System.Security.Cryptography;
using System.Text;

namespace BuoyAI.Infrastructure.Services.Bridge;

/// <summary>
/// Opens the batches the BuoyAI Cloud bridge seals to this server. RFC 9180 HPKE, base mode, single-shot:
/// DHKEM(P-256, HKDF-SHA256) 0x0010, HKDF-SHA256 0x0001, AES-128-GCM 0x0001. .NET has no HPKE in its base library, so
/// this is the RFC construction over the primitives it does have. The wire form is
/// <c>enc (65 bytes) | ciphertext | tag (16)</c>. The Cloud side is the Cloud Bridge service;
/// docs/self_host/bridge.md holds a test vector both sides decrypt, so the two cannot drift.
/// </summary>
public static class BridgeHpke
{
  private const int Nk = 16;
  private const int Nn = 12;
  private const int Nh = 32;
  private const int EncLength = 65;
  private static readonly byte[] Label = Encoding.ASCII.GetBytes("HPKE-v1");
  private static readonly byte[] KemSuite = [.. Encoding.ASCII.GetBytes("KEM"), 0x00, 0x10];
  private static readonly byte[] HpkeSuite = [.. Encoding.ASCII.GetBytes("HPKE"), 0x00, 0x10, 0x00, 0x01, 0x00, 0x01];

  /// <summary>HPKE <c>info</c> for the bank feed.</summary>
  public static readonly byte[] FeedInfo = Encoding.ASCII.GetBytes("buoymoney-feed-v1");

  /// <summary>The context the Cloud binds to a batch: server, batch, item and the cursor the batch ends at.</summary>
  public static byte[] FeedAad(Guid serverId, Guid batchId, Guid itemId, string cursorAfter) =>
    Encoding.UTF8.GetBytes($"{serverId:N}|{batchId:N}|{itemId:N}|{cursorAfter}");

  public static byte[] Open(ECDiffieHellman recipientPrivate, byte[] sealedBytes, byte[] info, byte[] aad)
  {
    if (sealedBytes.Length < EncLength + 16) throw new CryptographicException("Sealed message too short.");
    var enc = sealedBytes[..EncLength];
    if (enc[0] != 0x04) throw new CryptographicException("Bad encapsulated key.");
    using var ephemeralPublic = ECDiffieHellman.Create(new ECParameters
    {
      Curve = ECCurve.NamedCurves.nistP256,
      Q = new ECPoint { X = enc[1..33], Y = enc[33..65] }
    });
    var dh = recipientPrivate.DeriveRawSecretAgreement(ephemeralPublic.PublicKey);
    var pkRm = Point(recipientPrivate.ExportParameters(false));
    var (key, baseNonce) = KeySchedule(ExtractAndExpand(dh, [.. enc, .. pkRm]), info);
    var ct = sealedBytes[EncLength..^16];
    var tag = sealedBytes[^16..];
    var plain = new byte[ct.Length];
    using var aes = new AesGcm(key, 16);
    aes.Decrypt(baseNonce, ct, tag, plain, aad);
    return plain;
  }

  /// <summary>Test seam: the sealing half, so a fake Cloud can make batches. The real sealer lives in the Cloud.</summary>
  internal static byte[] Seal(byte[] recipientPublicPoint, byte[] info, byte[] aad, byte[] plaintext)
  {
    using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    return SealWith(ephemeral, recipientPublicPoint, info, aad, plaintext);
  }

  /// <summary>Test seam: seals with a caller-chosen ephemeral key so the RFC 9180 vectors can be reproduced byte for byte.</summary>
  internal static byte[] SealWith(ECDiffieHellman ephemeral, byte[] recipientPublicPoint, byte[] info, byte[] aad, byte[] plaintext)
  {
    using var recipient = ECDiffieHellman.Create(new ECParameters
    {
      Curve = ECCurve.NamedCurves.nistP256,
      Q = new ECPoint { X = recipientPublicPoint[1..33], Y = recipientPublicPoint[33..65] }
    });
    var enc = Point(ephemeral.ExportParameters(false));
    var dh = ephemeral.DeriveRawSecretAgreement(recipient.PublicKey);
    var (key, baseNonce) = KeySchedule(ExtractAndExpand(dh, [.. enc, .. recipientPublicPoint]), info);
    var ct = new byte[plaintext.Length];
    var tag = new byte[16];
    using var aes = new AesGcm(key, 16);
    aes.Encrypt(baseNonce, plaintext, ct, tag, aad);
    return [.. enc, .. ct, .. tag];
  }

  private static byte[] ExtractAndExpand(byte[] dh, byte[] kemContext)
  {
    var eaePrk = LabeledExtract(KemSuite, [], "eae_prk", dh);
    return LabeledExpand(KemSuite, eaePrk, "shared_secret", kemContext, Nh);
  }

  private static (byte[] Key, byte[] BaseNonce) KeySchedule(byte[] sharedSecret, byte[] info)
  {
    var pskIdHash = LabeledExtract(HpkeSuite, [], "psk_id_hash", []);
    var infoHash = LabeledExtract(HpkeSuite, [], "info_hash", info);
    byte[] context = [0x00, .. pskIdHash, .. infoHash];
    var secret = LabeledExtract(HpkeSuite, sharedSecret, "secret", []);
    return (LabeledExpand(HpkeSuite, secret, "key", context, Nk), LabeledExpand(HpkeSuite, secret, "base_nonce", context, Nn));
  }

  private static byte[] LabeledExtract(byte[] suiteId, byte[] salt, string label, byte[] ikm) =>
    HKDF.Extract(HashAlgorithmName.SHA256, [.. Label, .. suiteId, .. Encoding.ASCII.GetBytes(label), .. ikm], salt);

  private static byte[] LabeledExpand(byte[] suiteId, byte[] prk, string label, byte[] info, int length) =>
    HKDF.Expand(HashAlgorithmName.SHA256, prk, length, [(byte)(length >> 8), (byte)length, .. Label, .. suiteId, .. Encoding.ASCII.GetBytes(label), .. info]);

  private static byte[] Point(ECParameters p)
  {
    var raw = new byte[65];
    raw[0] = 0x04;
    p.Q.X!.CopyTo(raw, 1 + 32 - p.Q.X.Length);
    p.Q.Y!.CopyTo(raw, 33 + 32 - p.Q.Y.Length);
    return raw;
  }
}
