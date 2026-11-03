using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Reads and writes the two MFA secrets that used to be stored as plain text:
/// <c>Users.TotpSecret</c> (base32) and <c>Users.RecoveryCodes</c> (codes joined with ';').
///
/// Unlike the Plaid and Apple columns, these never went through IEncryptionService, so there is no
/// legacy ciphertext to guess at: a value is encrypted exactly when it carries the "v2:" prefix, and
/// anything else is legacy plaintext. Base32 and lowercase hex can never start with "v2:".
///
/// Writes only encrypt once <c>Encryption:WriteV2</c> is true. Until then the old copy running during
/// a blue-green overlap (or a rollback target) still reads these columns as plain text, so we keep
/// writing plain text, exactly as before.
/// </summary>
public static class MfaSecretProtector
{
  /// <summary>The value to store: encrypted when the rollout allows it, otherwise unchanged.</summary>
  public static string Protect(IEncryptionService crypto, string plain) =>
    crypto.WritesCurrentFormat && !string.IsNullOrEmpty(plain) ? crypto.Encrypt(plain) : plain;

  /// <summary>
  /// The usable secret for a stored value. Plain text passes through. A "v2:" value is decrypted, and
  /// throws if it was tampered with or the key is wrong, so callers treat that as a failed check.
  /// </summary>
  public static string Reveal(IEncryptionService crypto, string stored) =>
    crypto.IsCurrentFormat(stored) ? crypto.Decrypt(stored) : stored;
}
