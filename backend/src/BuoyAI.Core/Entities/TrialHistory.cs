using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Durable record that a free trial was once granted to an email address.
///
/// This table intentionally survives account deletion. When a user deletes their
/// account the Users row is anonymized (email rewritten to deleted-{id}@deleted.local)
/// and eventually hard-deleted, so the Users table alone cannot tell us whether a
/// re-registering email has already consumed its lifetime free trial. We persist only
/// a one-way hash of the normalized email here — never the plaintext address — so the
/// anti-abuse signal is retained without keeping a deleted user's PII.
/// </summary>
public class TrialHistory
{
    public int Id { get; set; }

    /// <summary>
    /// SHA-256 (hex, lowercase) of the normalized email. Unique. See
    /// <c>BuoyAI.Core.Utilities.TrialEmailHasher</c> for the canonical computation.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string EmailHash { get; set; } = string.Empty;

    /// <summary>When the first trial for this email was granted.</summary>
    public DateTime FirstTrialStartedAt { get; set; }

    /// <summary>
    /// When the original trial was scheduled to end. If a user deletes their account
    /// mid-trial and re-registers, the trial is resumed up to this date (never beyond),
    /// so they keep their remaining days without being able to obtain a fresh trial.
    /// </summary>
    public DateTime? OriginalTrialEndsAt { get; set; }

    /// <summary>When the most recent trial grant attempt was recorded.</summary>
    public DateTime LastTrialGrantedAt { get; set; }

    /// <summary>How many times a trial has been granted to this email (normally 1).</summary>
    public int TrialGrantCount { get; set; } = 1;

    /// <summary>The first user id that consumed a trial on this email (informational).</summary>
    public int? FirstUserId { get; set; }

    /// <summary>Registration source of the first trial: "email", "google", or "apple".</summary>
    [MaxLength(20)]
    public string? FirstSource { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
