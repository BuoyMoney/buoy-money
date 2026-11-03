using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.DTOs;

/// <summary>Registers (or refreshes) an FCM device token for the current user.</summary>
public class RegisterDeviceRequest
{
  [Required]
  public string Token { get; set; } = string.Empty;

  /// <summary>"android" or "ios"; "relay" for a self-hosted server using the push relay (Token is then the relay pairing credential).</summary>
  [Required]
  public string Platform { get; set; } = string.Empty;

  /// <summary>Relay devices: the phone's P-256 public key, base64 of the 65-byte uncompressed point.</summary>
  public string? PublicKey { get; set; }

  /// <summary>
  /// The device's current UTC offset in minutes (e.g. -300 for EST). Keeps quiet-hours
  /// evaluation anchored to the user's local clock without shipping IANA zone data.
  /// </summary>
  public int? TimeZoneOffsetMinutes { get; set; }

  /// <summary>
  /// IANA timezone id (e.g. "America/Chicago"). Preferred over the fixed offset when
  /// present: it tracks DST instantly instead of drifting until the next app start. (#941)
  /// </summary>
  public string? TimeZoneId { get; set; }
}

/// <summary>Unregisters a device token (logout, or the user disabling push on-device).</summary>
public class UnregisterDeviceRequest
{
  [Required]
  public string Token { get; set; } = string.Empty;
}

public class PushPreferencesDto
{
  public bool PushEnabled { get; set; }
  public bool ConnectionIssues { get; set; }
  public bool BudgetAlerts { get; set; }
  public bool BillReminders { get; set; }
  public bool SubscriptionDetection { get; set; }
  public bool TransactionReview { get; set; }
  public bool QuietHoursEnabled { get; set; }
  public int QuietHoursStartMinutes { get; set; }
  public int QuietHoursEndMinutes { get; set; }

  /// <summary>Number of devices currently registered for push (informational, for the UI).</summary>
  public int RegisteredDeviceCount { get; set; }
}

/// <summary>Full-object update, mirroring the email-preferences PUT shape.</summary>
public class UpdatePushPreferencesRequest
{
  public bool PushEnabled { get; set; } = true;
  public bool ConnectionIssues { get; set; } = true;
  public bool BudgetAlerts { get; set; } = true;
  public bool BillReminders { get; set; } = true;
  public bool SubscriptionDetection { get; set; } = true;
  public bool TransactionReview { get; set; } = true;
  public bool QuietHoursEnabled { get; set; } = false;

  [Range(0, 1439)]
  public int QuietHoursStartMinutes { get; set; } = 22 * 60;

  [Range(0, 1439)]
  public int QuietHoursEndMinutes { get; set; } = 7 * 60;

  /// <summary>See <see cref="RegisterDeviceRequest.TimeZoneOffsetMinutes"/>.</summary>
  public int? TimeZoneOffsetMinutes { get; set; }

  /// <summary>See <see cref="RegisterDeviceRequest.TimeZoneId"/>.</summary>
  public string? TimeZoneId { get; set; }
}
