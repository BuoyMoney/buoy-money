namespace BuoyAI.Core.Entities;

/// <summary>
/// Idempotency ledger for Stripe webhook events. One row per processed Stripe event
/// id (<c>evt_...</c>). The webhook handler records an event only after it processes
/// successfully and skips any event whose id is already present, so Stripe
/// redeliveries and at-least-once duplicates never re-run handlers (which would
/// double-insert payment rows and re-send emails). A handler that throws leaves the
/// event unrecorded, so genuine-failure retries still re-process.
/// </summary>
public class ProcessedStripeEvent
{
  /// <summary>The Stripe event id (<c>evt_...</c>) — unique and stable across redeliveries.</summary>
  public string EventId { get; set; } = string.Empty;

  /// <summary>The Stripe event type (e.g. <c>invoice.payment_succeeded</c>), for diagnostics.</summary>
  public string EventType { get; set; } = string.Empty;

  public DateTime ProcessedAt { get; set; }
}
