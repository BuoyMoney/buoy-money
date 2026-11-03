using BuoyAI.Core.DTOs;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Sends a push copy of in-app notifications to the user's registered devices via FCM,
/// honoring per-user preferences and quiet hours. The in-app notification itself is never
/// gated here — push is an additional delivery channel, and every method is best-effort:
/// failures are logged, never thrown into the notification-creation path.
/// </summary>
public interface IPushNotificationService
{
  /// <summary>
  /// Push <paramref name="title"/>/<paramref name="body"/> to every device of the user,
  /// gated by their preferences for <paramref name="notificationType"/> (the in-app
  /// notification Type string, e.g. "budget_alert"). During the user's quiet hours the
  /// push is queued and delivered when the window ends. Never throws.
  /// </summary>
  Task SendToUserAsync(int userId, string notificationType, string title, string body,
    string? actionUrl, CancellationToken cancellationToken = default);

  Task RegisterDeviceAsync(int userId, RegisterDeviceRequest request);

  /// <summary>Returns false when the token wasn't registered to this user.</summary>
  Task<bool> UnregisterDeviceAsync(int userId, string token);

  Task<PushPreferencesDto> GetPreferencesAsync(int userId);

  Task<PushPreferencesDto> UpdatePreferencesAsync(int userId, UpdatePushPreferencesRequest request);

  /// <summary>Delivers queued (quiet-hours) pushes that are now due. Returns the count sent.</summary>
  Task<int> DeliverDueQueuedAsync(CancellationToken cancellationToken = default);

  /// <summary>Deletes tokens not seen for 270+ days (FCM's own staleness horizon). Returns the count removed.</summary>
  Task<int> PruneStaleTokensAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Thin wrapper over the FCM Admin SDK so the push pipeline is unit-testable and the app
/// degrades to a no-op when Firebase credentials aren't configured.
/// </summary>
public interface IFcmSender
{
  /// <summary>False when Firebase credentials are absent — every send becomes a no-op.</summary>
  bool IsEnabled { get; }

  /// <summary>
  /// Sends one notification message to up to 500 tokens. Returns the tokens FCM reported
  /// as permanently invalid (Unregistered / InvalidArgument) so the caller can prune them.
  /// </summary>
  Task<FcmSendResult> SendAsync(IReadOnlyList<string> tokens, string title, string body,
    IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default);
}

public record FcmSendResult(int SuccessCount, int FailureCount, IReadOnlyList<string> InvalidTokens)
{
  public static readonly FcmSendResult Empty = new(0, 0, Array.Empty<string>());
}
