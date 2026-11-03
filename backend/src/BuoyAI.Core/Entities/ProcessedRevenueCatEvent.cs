namespace BuoyAI.Core.Entities;

/// <summary>
/// Idempotency ledger for RevenueCat webhook events, the mirror of
/// <see cref="ProcessedStripeEvent"/>. RevenueCat retries any non-200 up to five times, and
/// a slow response is also retried, so the same event id can arrive more than once. A row is
/// written only after the event applied cleanly; a handler that throws leaves it unrecorded
/// so the retry re-runs.
/// </summary>
public class ProcessedRevenueCatEvent
{
  public string EventId { get; set; } = string.Empty;
  public string EventType { get; set; } = string.Empty;
  public DateTime ProcessedAt { get; set; }
}
