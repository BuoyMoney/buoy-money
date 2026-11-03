using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// An email captured on the marketing site in exchange for an extended free trial.
///
/// This is the top of the funnel: a visitor who is not ready to create an account but
/// will trade an address for a better offer. The row is the durable record of that
/// promise, so when the same address eventually registers we can honour the longer
/// trial instead of the configured default.
///
/// Unlike <see cref="TrialHistory"/> (which stores only a hash, because it must survive
/// account deletion as an anti-abuse signal), this table keeps the plaintext address:
/// it is a marketing contact the visitor knowingly gave us and which we have to be able
/// to email. It is deleted with the account.
/// </summary>
public class MarketingLead
{
    public int Id { get; set; }

    /// <summary>Normalized (trimmed, lowercased) email. Unique.</summary>
    [Required]
    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Where the capture happened: "popup_timed", "popup_exit", or "calculator_exit".
    /// Kept so we can tell which trigger actually earns leads before spending on ads.
    /// </summary>
    [MaxLength(40)]
    public string? Source { get; set; }

    /// <summary>
    /// Trial length promised to this address, in days. Snapshotted at capture time so a
    /// later change to the configured offer never retroactively shortens what someone
    /// was told they would get. Rows created before 2026-10-15 UTC may carry up to 60 days
    /// (the old popup offer, still honoured); newer rows carry Subscription:ExtendedTrialPeriodDays (30).
    /// </summary>
    public int OfferedTrialDays { get; set; }

    /// <summary>
    /// Set when the address registered and the extended trial was actually granted.
    /// A redeemed lead is never honoured a second time.
    /// </summary>
    public DateTime? RedeemedAt { get; set; }

    /// <summary>The user that consumed the offer (informational).</summary>
    public int? RedeemedByUserId { get; set; }

    /// <summary>
    /// The calculator inputs/results at capture time, as JSON, when the lead came from
    /// the calculator's "save these numbers" prompt. Null for the plain popup. Used to
    /// personalise the confirmation email; never required.
    /// </summary>
    public string? CalculatorSnapshot { get; set; }

    /// <summary>Null until the confirmation email goes out, so a send failure can be retried.</summary>
    public DateTime? ConfirmationSentAt { get; set; }

    /// <summary>
    /// When we reminded this lead that their reserved trial is still waiting. NULL = not
    /// yet reminded.
    /// </summary>
    /// <remarks>
    /// Capture sends one confirmation and nothing after it, so a visitor who handed over
    /// their email and did not register that day was never contacted again. A periodic
    /// sweep delivers the reminder, and this marker is what stops it re-sending on every
    /// pass. Sent once, ever: one ignored nudge is a non-event, a recurring one is a
    /// reason to unsubscribe.
    /// </remarks>
    public DateTime? ReminderSentAt { get; set; }

    /// <summary>
    /// Random opaque token embedded in the unsubscribe link. Deliberately NOT derived from the
    /// email: a derived value in a URL would let anyone unsubscribe (or probe for) an arbitrary
    /// address by computing it.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string UnsubscribeToken { get; set; } = string.Empty;

    /// <summary>Set when the lead opts out. Suppresses all further marketing sends.</summary>
    public DateTime? UnsubscribedAt { get; set; }

    /// <summary>How many times this address has been submitted (repeat submits are idempotent).</summary>
    public int CaptureCount { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
