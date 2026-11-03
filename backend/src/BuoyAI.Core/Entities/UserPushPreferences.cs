namespace BuoyAI.Core.Entities;

/// <summary>
/// Per-user push notification preferences: a master toggle, per-category toggles that
/// gate notification Types (see PushNotificationService.CategoryForType), and a local-time
/// quiet-hours window during which pushes are queued instead of sent.
/// An absent row means defaults: everything on, no quiet hours.
/// The in-app notification (bell) is never gated by any of this — only the push copy is.
/// </summary>
public class UserPushPreferences
{
  public int Id { get; set; }
  public int UserId { get; set; }

  public bool PushEnabled { get; set; } = true;

  // Category toggles
  public bool ConnectionIssues { get; set; } = true;
  public bool BudgetAlerts { get; set; } = true;
  public bool BillReminders { get; set; } = true;
  public bool SubscriptionDetection { get; set; } = true;
  public bool TransactionReview { get; set; } = true;

  // Quiet hours, minutes-from-midnight in the user's LOCAL time. Start > end means the
  // window wraps past midnight (the 22:00 → 07:00 default).
  public bool QuietHoursEnabled { get; set; } = false;
  public int QuietHoursStartMinutes { get; set; } = 22 * 60;
  public int QuietHoursEndMinutes { get; set; } = 7 * 60;

  /// <summary>
  /// The device's UTC offset in minutes at the last prefs write or device registration.
  /// Refreshed on every app start, so DST drift self-heals within a day of the change.
  /// </summary>
  public int TimeZoneOffsetMinutes { get; set; } = 0;

  /// <summary>
  /// IANA timezone id (e.g. "America/Chicago"), sent by clients that can resolve it.
  /// Preferred over <see cref="TimeZoneOffsetMinutes"/> when present: a zone id tracks
  /// DST transitions instantly instead of waiting for the next app start. (#941)
  /// </summary>
  public string? TimeZoneId { get; set; }

  public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
  public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
