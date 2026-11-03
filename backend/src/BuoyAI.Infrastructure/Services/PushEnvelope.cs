using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.Utilities;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The encrypted push payload a self-hosted server hands to the push relay (wire contract in
/// docs/self_host/push_relay.md). Only the phone holds the private key, so the relay and Google or
/// Apple relay ciphertext and nothing else.
///
/// Format (then base64url, no padding):
///   0x01 | ephemeral P-256 public key (65 bytes, uncompressed) | nonce (12) | ciphertext | tag (16)
/// Key: ECDH(ephemeral private, phone public) x-coordinate, then HKDF-SHA256 with the ephemeral
/// public key as salt and "buoymoney-push-v1" as info, 32 bytes. Cipher: AES-256-GCM with the
/// version byte as associated data. Plaintext: UTF-8 JSON {"title","body","data":{...},"ts":unix}.
/// </summary>
public static class PushEnvelope
{
  private const byte Version = 1;
  private const int PublicKeyLength = 65;
  private const int NonceLength = 12;
  private const int TagLength = 16;
  private static readonly byte[] Info = Encoding.ASCII.GetBytes("buoymoney-push-v1");

  /// <summary>Keeps the whole message inside the 4 KB data limit of FCM and APNs after base64.</summary>
  public const int MaxPlaintextBytes = 2000;

  /// <summary>Title and body are each cut to this many characters before sealing.</summary>
  public const int MaxTextChars = 200;

  public static string Seal(string phonePublicKey, string title, string body, IReadOnlyDictionary<string, string> data, DateTimeOffset? now = null)
  {
    var ts = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
    // Long text is cut, never dropped: the in-app notification holds the full text. The cut keeps
    // surrogate pairs whole so the UTF-8 serialization cannot fail.
    title = SafeText.Truncate(title, MaxTextChars);
    body = SafeText.Truncate(body, MaxTextChars);
    var plaintext = JsonSerializer.SerializeToUtf8Bytes(new { title, body, data, ts });
    if (plaintext.Length > MaxPlaintextBytes) throw new ArgumentException("Push payload is too large.");

    using var phone = ImportPublicKey(phonePublicKey);
    using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    var ephemeralPublic = ExportPublicKey(ephemeral);
    var secret = ephemeral.DeriveRawSecretAgreement(phone.PublicKey);
    var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt: ephemeralPublic, info: Info);

    var nonce = RandomNumberGenerator.GetBytes(NonceLength);
    var ciphertext = new byte[plaintext.Length];
    var tag = new byte[TagLength];
    using (var aes = new AesGcm(key, TagLength))
    {
      aes.Encrypt(nonce, plaintext, ciphertext, tag, new[] { Version });
    }

    var output = new byte[1 + PublicKeyLength + NonceLength + ciphertext.Length + TagLength];
    output[0] = Version;
    ephemeralPublic.CopyTo(output, 1);
    nonce.CopyTo(output, 1 + PublicKeyLength);
    ciphertext.CopyTo(output, 1 + PublicKeyLength + NonceLength);
    tag.CopyTo(output, 1 + PublicKeyLength + NonceLength + ciphertext.Length);
    return Base64Url(output);
  }

  /// <summary>True when the text is a base64 (or base64url) uncompressed P-256 point that is on the curve.</summary>
  public static bool IsValidPublicKey(string? value)
  {
    try
    {
      using var key = ImportPublicKey(value ?? "");
      return true;
    }
    catch (Exception)
    {
      return false;
    }
  }

  private static ECDiffieHellman ImportPublicKey(string value)
  {
    var raw = FromBase64Url(value);
    if (raw.Length != PublicKeyLength || raw[0] != 0x04) throw new ArgumentException("Public key must be an uncompressed P-256 point.");
    var ecdh = ECDiffieHellman.Create();
    ecdh.ImportParameters(new ECParameters
    {
      Curve = ECCurve.NamedCurves.nistP256,
      Q = new ECPoint { X = raw[1..33], Y = raw[33..65] }
    });
    return ecdh;
  }

  private static byte[] ExportPublicKey(ECDiffieHellman key)
  {
    var p = key.ExportParameters(false);
    var raw = new byte[PublicKeyLength];
    raw[0] = 0x04;
    p.Q.X!.CopyTo(raw, 1 + (32 - p.Q.X.Length));
    p.Q.Y!.CopyTo(raw, 33 + (32 - p.Q.Y.Length));
    return raw;
  }

  private static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

  private static byte[] FromBase64Url(string value)
  {
    var s = value.Trim().Replace('-', '+').Replace('_', '/');
    s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    return Convert.FromBase64String(s);
  }
}
