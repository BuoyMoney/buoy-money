using Microsoft.Extensions.Configuration;
using Stripe;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The thin edge between <see cref="ReservationService"/> and Stripe. The service builds the
/// Checkout options; this seam sends them, so tests can capture exactly what would be sent.
/// </summary>
public interface IReservationCheckoutClient
{
  /// <summary>Creates a Checkout Session and returns its hosted URL.</summary>
  Task<string> CreateSessionAsync(Stripe.Checkout.SessionCreateOptions options, CancellationToken ct = default);

  /// <summary>
  /// Returns the metadata of a payment intent, or null when Stripe is not configured or the id is empty.
  /// Throws <see cref="InvalidOperationException"/> on a Stripe failure so the webhook retries.
  /// </summary>
  Task<IReadOnlyDictionary<string, string>?> GetPaymentIntentMetadataAsync(
    string paymentIntentId, CancellationToken ct = default);
}

/// <summary>Stripe implementation. The key is read at call time so the app boots without Stripe configured.</summary>
public class StripeReservationCheckoutClient : IReservationCheckoutClient
{
  private readonly IConfiguration _configuration;

  public StripeReservationCheckoutClient(IConfiguration configuration)
  {
    _configuration = configuration;
  }

  public async Task<string> CreateSessionAsync(Stripe.Checkout.SessionCreateOptions options, CancellationToken ct = default)
  {
    var apiKey = _configuration["Stripe:SecretKey"];
    if (string.IsNullOrEmpty(apiKey))
    {
      throw new InvalidOperationException("Stripe API key not configured");
    }

    var session = await new StripeClient(apiKey).V1.Checkout.Sessions.CreateAsync(options, cancellationToken: ct);
    return session.Url;
  }

  public async Task<IReadOnlyDictionary<string, string>?> GetPaymentIntentMetadataAsync(
    string paymentIntentId, CancellationToken ct = default)
  {
    var apiKey = _configuration["Stripe:SecretKey"];
    if (string.IsNullOrEmpty(paymentIntentId) || string.IsNullOrEmpty(apiKey))
    {
      return null;
    }

    try
    {
      var intent = await new StripeClient(apiKey).V1.PaymentIntents.GetAsync(paymentIntentId, cancellationToken: ct);
      return intent.Metadata?.ToDictionary(kv => kv.Key, kv => kv.Value);
    }
    catch (StripeException ex)
    {
      throw new InvalidOperationException($"Could not look up payment intent {paymentIntentId}", ex);
    }
  }
}
