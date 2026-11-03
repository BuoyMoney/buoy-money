using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <inheritdoc />
public class ReservationService : IReservationService
{
  public const string MetadataKind = "reservation";

  private const string SubmitMessage =
    "We save your card so you can start a plan in one tap later. We won't charge it again " +
    "unless you choose a plan. Your $1 comes off your first year and is refundable any time.";

  private readonly BuoyAIDbContext _context;
  private readonly ReservationOptions _options;
  private readonly IReservationCheckoutClient _checkoutClient;
  private readonly ILogger<ReservationService> _logger;
  private readonly string _environmentTag;
  private readonly FoundingOptions _founding;

  // The Stripe key is read at call time, so the service can be built when Stripe is not
  // configured (local dev, tests) and only fails if someone actually tries to check out.
  public ReservationService(
    BuoyAIDbContext context,
    IOptions<ReservationOptions> options,
    IReservationCheckoutClient checkoutClient,
    ILogger<ReservationService> logger,
    IHostEnvironment hostEnvironment,
    IOptions<FoundingOptions>? founding = null)
  {
    _founding = founding?.Value ?? new FoundingOptions();
    _context = context;
    _options = options.Value;
    _checkoutClient = checkoutClient;
    _logger = logger;
    // Staging and production share one live Stripe account, so each session is stamped with
    // the environment that created it and each environment's webhook records only its own.
    _environmentTag = StripeEnvironmentPolicy.Tag(hostEnvironment.EnvironmentName);
  }

  public bool IsEnabled => _options.Enabled;

  public async Task<string> CreateCheckoutAsync(string? choice, string? source, CancellationToken ct = default)
  {
    if (!_options.Enabled)
    {
      throw new ReservationsDisabledException();
    }

    if (!ReservationChoices.IsValid(choice))
    {
      throw new InvalidReservationChoiceException(choice);
    }

    // Spot cap. The rule for the race: a spot is "taken" only once the webhook has recorded a PAID reservation,
    // and a checkout is refused only when taken >= limit at the moment it STARTS. We do not hold spots for open
    // Stripe sessions (that would need a Stripe lookup per request, and abandoned sessions would lock real spots
    // until they expire). So when several people start checkout at 149, all of them can pay and the count can
    // overshoot by at most the number of checkouts in flight at that moment. Every paid reservation past the limit
    // is still honored as founding (HasPaidReservationAsync does not know about the limit) and nobody is refunded
    // or revoked automatically. The owner can refund an overshooter by hand if wanted.
    var status = await GetFoundingStatusAsync(ct);
    if (!status.Open)
    {
      throw new FoundingSpotsTakenException(status.Limit);
    }

    // Untrusted text: the surrogate-safe helper never leaves half an emoji behind.
    var cleanSource = string.IsNullOrWhiteSpace(source) ? "direct" : SafeText.Truncate(source.Trim(), 100);

    var options = new Stripe.Checkout.SessionCreateOptions
    {
      Mode = "payment",
      // Card only: the offer saves a card, and delayed methods would complete the session unpaid.
      PaymentMethodTypes = ["card"],
      CustomerCreation = "always",
      SuccessUrl = _options.SuccessUrl,
      CancelUrl = _options.CancelUrl,
      LineItems =
      [
        new Stripe.Checkout.SessionLineItemOptions
        {
          Quantity = 1,
          PriceData = new Stripe.Checkout.SessionLineItemPriceDataOptions
          {
            Currency = _options.Currency,
            UnitAmount = _options.AmountCents,
            ProductData = new Stripe.Checkout.SessionLineItemPriceDataProductDataOptions
            {
              Name = _options.ProductName,
            },
          },
        },
      ],
      PaymentIntentData = new Stripe.Checkout.SessionPaymentIntentDataOptions
      {
        SetupFutureUsage = "off_session",
        Metadata = new Dictionary<string, string>
        {
          ["kind"] = MetadataKind,
          [StripeEnvironmentPolicy.MetadataKey] = _environmentTag,
        },
      },
      CustomText = new Stripe.Checkout.SessionCustomTextOptions
      {
        Submit = new Stripe.Checkout.SessionCustomTextSubmitOptions { Message = SubmitMessage },
      },
      Metadata = new Dictionary<string, string>
      {
        ["kind"] = MetadataKind,
        ["choice"] = choice!,
        ["source"] = cleanSource,
        [StripeEnvironmentPolicy.MetadataKey] = _environmentTag,
      },
    };

    var url = await _checkoutClient.CreateSessionAsync(options, ct);
    _logger.LogInformation("Created reservation checkout session (choice {Choice})", choice);
    return url;
  }

  public async Task RecordCompletedAsync(CompletedReservationCheckout checkout, CancellationToken ct = default)
  {
    var metadata = checkout.Metadata?.ToDictionary(kv => kv.Key, kv => kv.Value);
    if (!StripeEnvironmentPolicy.IsOwnedBy(metadata, _environmentTag))
    {
      _logger.LogInformation(
        "Skipping reservation session {SessionId}: it belongs to another environment", checkout.SessionId);
      return;
    }

    if (await _context.Reservations.AnyAsync(r => r.StripeCheckoutSessionId == checkout.SessionId, ct))
    {
      return;
    }

    string? choice = null;
    string? source = null;
    checkout.Metadata?.TryGetValue("choice", out choice);
    checkout.Metadata?.TryGetValue("source", out source);

    _context.Reservations.Add(new Reservation
    {
      Email = checkout.Email,
      StripeCheckoutSessionId = checkout.SessionId,
      StripeCustomerId = checkout.CustomerId,
      StripePaymentIntentId = checkout.PaymentIntentId,
      AmountCents = (int)(checkout.AmountTotal ?? _options.AmountCents),
      Currency = string.IsNullOrEmpty(checkout.Currency) ? _options.Currency : checkout.Currency,
      PlanChoice = ReservationChoices.IsValid(choice) ? choice! : "unsure",
      Source = source,
      Status = ReservationStatus.Paid,
      CreatedAt = DateTime.UtcNow,
    });

    try
    {
      await _context.SaveChangesAsync(ct);
    }
    catch (DbUpdateException)
    {
      // A parallel delivery of the same session may have won the unique index. If the row
      // exists now, that is the idempotent case; anything else is a real failure.
      _context.ChangeTracker.Clear();
      if (!await _context.Reservations.AnyAsync(r => r.StripeCheckoutSessionId == checkout.SessionId, ct))
      {
        throw;
      }

      _logger.LogInformation("Reservation for session {SessionId} was recorded concurrently", checkout.SessionId);
    }
  }

  public async Task<bool> MarkRefundedAsync(string paymentIntentId, CancellationToken ct = default)
  {
    if (string.IsNullOrEmpty(paymentIntentId))
    {
      return false;
    }

    var reservation = await _context.Reservations
      .FirstOrDefaultAsync(r => r.StripePaymentIntentId == paymentIntentId, ct);
    if (reservation == null)
    {
      return false;
    }

    if (reservation.Status != ReservationStatus.Refunded)
    {
      reservation.Status = ReservationStatus.Refunded;
      reservation.RefundedAt = DateTime.UtcNow;
      await _context.SaveChangesAsync(ct);
    }

    return true;
  }

  public async Task<bool> IsReservationPaymentAsync(string paymentIntentId, CancellationToken ct = default)
  {
    var metadata = await _checkoutClient.GetPaymentIntentMetadataAsync(paymentIntentId, ct);
    return metadata != null
      && metadata.TryGetValue("kind", out var kind)
      && kind == MetadataKind
      && StripeEnvironmentPolicy.IsOwnedBy(metadata.ToDictionary(kv => kv.Key, kv => kv.Value), _environmentTag);
  }

  public async Task<FoundingStatus> GetFoundingStatusAsync(CancellationToken ct = default)
  {
    var limit = _founding.EffectiveLimit;
    var taken = await GetCountAsync(ct);
    var remaining = Math.Max(0, limit - taken);
    return new FoundingStatus(limit, taken, remaining, _options.Enabled && remaining > 0);
  }

  // Same rule as HasPaidReservationAsync (Status == Paid), so the cap and eligibility agree.
  public Task<int> GetCountAsync(CancellationToken ct = default) =>
    _context.Reservations.CountAsync(r => r.Status == ReservationStatus.Paid, ct);

  public Task<bool> HasPaidReservationAsync(string? email, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(email))
    {
      return Task.FromResult(false);
    }

    var normalized = email.Trim().ToLowerInvariant();
    return _context.Reservations.AnyAsync(
      r => r.Status == ReservationStatus.Paid && r.Email != null && r.Email.ToLower() == normalized, ct);
  }

  public async Task<long> GetPaidReservationCreditCentsAsync(string? email, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(email))
    {
      return 0;
    }

    var normalized = email.Trim().ToLowerInvariant();
    // One credit per person, however many times they reserved: the earliest paid reservation.
    var amount = await _context.Reservations
      .Where(r => r.Status == ReservationStatus.Paid && r.Email != null && r.Email.ToLower() == normalized)
      .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
      .Select(r => (int?)r.AmountCents)
      .FirstOrDefaultAsync(ct);
    return Math.Max(0, amount ?? 0);
  }
}
