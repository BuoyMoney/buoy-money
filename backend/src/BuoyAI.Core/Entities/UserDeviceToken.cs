namespace BuoyAI.Core.Entities;

/// <summary>
/// An FCM registration token for one app install (Android or iOS) belonging to a user.
/// Upserted on login / app start / token refresh; deleted when FCM reports the token
/// unregistered or after 270 days without contact (Google's own expiry horizon).
/// </summary>
public class UserDeviceToken
{
  public int Id { get; set; }
  public int UserId { get; set; }

  /// <summary>The FCM registration token. Unique — a token identifies one install.</summary>
  public string Token { get; set; } = string.Empty;

  /// <summary>"android", "ios", or "relay" (a push relay pairing credential, see docs/self_host/push_relay.md).</summary>
  public string Platform { get; set; } = string.Empty;

  /// <summary>
  /// Relay devices only: the phone's P-256 public key (base64 uncompressed point). The server
  /// encrypts each push to it, so the relay and the push services see only ciphertext.
  /// </summary>
  public string? PublicKey { get; set; }

  public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

  /// <summary>Refreshed every time the app re-registers the token (each start).</summary>
  public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
