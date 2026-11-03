namespace BuoyAI.Core.Entities;

/// <summary>
/// A founding-member reservation: a $1 Stripe Checkout payment made before launch.
/// One row per paid Checkout session.
/// </summary>
public class Reservation
{
  public int Id { get; set; }
  public string? Email { get; set; }
  public string StripeCheckoutSessionId { get; set; } = string.Empty;
  public string? StripeCustomerId { get; set; }
  public string? StripePaymentIntentId { get; set; }
  public int AmountCents { get; set; }
  public string Currency { get; set; } = "usd";

  /// <summary>What the person said they would pick at launch (see ReservationChoices).</summary>
  public string PlanChoice { get; set; } = "unsure";

  /// <summary>"paid" or "refunded" (see ReservationStatus).</summary>
  public string Status { get; set; } = ReservationStatus.Paid;
  public string? Source { get; set; }
  public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
  public DateTime? RefundedAt { get; set; }

  /// <summary>When this email was invited to the self-host closed beta (task 24). Null: not invited.</summary>
  public DateTime? BetaInvitedAt { get; set; }
}

public static class ReservationStatus
{
  public const string Paid = "paid";
  public const string Refunded = "refunded";
}

public static class ReservationChoices
{
  public static readonly string[] All = ["bank_sync", "ai", "both", "free", "unsure"];

  public static bool IsValid(string? choice) =>
    choice != null && Array.IndexOf(All, choice) >= 0;
}
