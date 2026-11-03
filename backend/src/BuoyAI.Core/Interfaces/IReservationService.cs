using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces;

/// <summary>Thrown when a reservation is requested while the feature is switched off.</summary>
public class ReservationsDisabledException : Exception
{
  public ReservationsDisabledException() : base("Reservations are not open yet.") { }
}

/// <summary>Thrown when the plan choice is not one we know.</summary>
public class InvalidReservationChoiceException : Exception
{
  public InvalidReservationChoiceException(string? choice)
    : base($"Unknown plan choice '{choice}'. Use one of: {string.Join(", ", ReservationChoices.All)}.") { }
}

/// <summary>Thrown when a founding checkout starts after every founding spot is taken.</summary>
public class FoundingSpotsTakenException : Exception
{
  public int Limit { get; }

  public FoundingSpotsTakenException(int limit)
    : base($"All {limit} founding spots are taken.") { Limit = limit; }
}

/// <summary>Public founding-spot numbers. No personal data.</summary>
public record FoundingStatus(int Limit, int Taken, int Remaining, bool Open);

/// <summary>The fields we keep from a completed reservation Checkout session.</summary>
public record CompletedReservationCheckout(
  string SessionId,
  string? Email,
  string? CustomerId,
  string? PaymentIntentId,
  long? AmountTotal,
  string? Currency,
  IReadOnlyDictionary<string, string>? Metadata);

/// <summary>Founding-member $1 reservations (Stripe Checkout, one-off payment).</summary>
public interface IReservationService
{
  bool IsEnabled { get; }

  /// <summary>Creates a Checkout Session and returns its hosted URL.</summary>
  Task<string> CreateCheckoutAsync(string? choice, string? source, CancellationToken ct = default);

  /// <summary>Stores a paid Checkout session as a reservation. Safe to call twice for the same session.</summary>
  Task RecordCompletedAsync(CompletedReservationCheckout checkout, CancellationToken ct = default);

  /// <summary>Marks the reservation paid with this payment intent as refunded. Returns false when there is none.</summary>
  Task<bool> MarkRefundedAsync(string paymentIntentId, CancellationToken ct = default);

  /// <summary>
  /// Whether a payment intent came from a reservation checkout (asks Stripe). Used when a refund
  /// arrives before its checkout event, so the refund can be retried instead of lost.
  /// </summary>
  Task<bool> IsReservationPaymentAsync(string paymentIntentId, CancellationToken ct = default);

  /// <summary>
  /// Founding spot numbers: the limit, spots taken (paid, not refunded: the same rule as founding eligibility),
  /// spots remaining, and whether a new checkout would be accepted right now.
  /// </summary>
  Task<FoundingStatus> GetFoundingStatusAsync(CancellationToken ct = default);

  /// <summary>Number of reservations that are paid and not refunded.</summary>
  Task<int> GetCountAsync(CancellationToken ct = default);

  /// <summary>
  /// Whether this email holds a paid, not refunded reservation. That is how a customer qualifies
  /// for founding prices. The caller must only pass an email it has verified belongs to the user.
  /// </summary>
  Task<bool> HasPaidReservationAsync(string? email, CancellationToken ct = default);

  /// <summary>
  /// The amount of this email's earliest paid, unrefunded reservation, in cents, or 0. It is credited once against the
  /// founding member's first Cloud payment. Same rule as <see cref="HasPaidReservationAsync"/>: pass a verified email only.
  /// </summary>
  Task<long> GetPaidReservationCreditCentsAsync(string? email, CancellationToken ct = default);
}
