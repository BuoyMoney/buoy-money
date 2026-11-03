namespace BuoyAI.Core.Entities;

/// <summary>
/// A push held back by the user's quiet hours. The PushQueueDeliveryService sweeper sends
/// it once DeliverAfter passes and deletes the row (or gives up after 3 failed attempts).
/// FCM has no native deferred delivery, so this queue is how quiet hours actually work.
/// </summary>
public class QueuedPushNotification
{
  public int Id { get; set; }
  public int UserId { get; set; }
  public string Title { get; set; } = string.Empty;
  public string Body { get; set; } = string.Empty;

  /// <summary>JSON object of FCM data fields (route, notificationId, type).</summary>
  public string DataJson { get; set; } = string.Empty;

  /// <summary>UTC instant after which the sweeper may deliver this push.</summary>
  public DateTime DeliverAfter { get; set; }

  public int Attempts { get; set; } = 0;
  public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
