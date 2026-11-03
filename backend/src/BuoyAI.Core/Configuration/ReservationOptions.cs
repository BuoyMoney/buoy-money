namespace BuoyAI.Core.Configuration;

/// <summary>
/// Settings for the founding-member reservation. Bound to the "Reservations" section.
/// Off by default: nothing is sold until Enabled is set to true.
/// </summary>
public class ReservationOptions
{
  public const string SectionName = "Reservations";

  public bool Enabled { get; set; }
  public int AmountCents { get; set; } = 100;
  public string Currency { get; set; } = "usd";
  public string ProductName { get; set; } = "Buoy Money founding member reservation";

  /// <summary>Where Stripe sends people after paying.</summary>
  public string SuccessUrl { get; set; } = "https://buoy-ai.com/founding/thanks";

  /// <summary>Where Stripe sends people who back out of Checkout.</summary>
  public string CancelUrl { get; set; } = "https://buoy-ai.com/founding";
}
