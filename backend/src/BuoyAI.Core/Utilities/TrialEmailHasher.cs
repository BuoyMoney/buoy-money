using System.Security.Cryptography;
using System.Text;

namespace BuoyAI.Core.Utilities;

/// <summary>
/// Canonical one-way hash of an email address for trial-abuse tracking.
///
/// Normalization MUST match how emails are stored elsewhere (lowercase + trim) so the
/// same address always produces the same hash regardless of how it was typed. We store
/// only this hash in <c>TrialHistory</c> — never the plaintext email — so a deleted
/// user's address is not retained while still letting us detect a repeat trial.
/// </summary>
public static class TrialEmailHasher
{
    /// <summary>
    /// Returns the lowercase hex SHA-256 of the normalized email, or null when the
    /// input is null/whitespace (nothing to track).
    /// </summary>
    public static string? Compute(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var normalized = email.Trim().ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
