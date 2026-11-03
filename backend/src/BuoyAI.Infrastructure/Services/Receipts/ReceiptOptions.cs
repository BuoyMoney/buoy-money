using Microsoft.Extensions.Configuration;

namespace BuoyAI.Infrastructure.Services.Receipts;

/// <summary>Receipt scanning settings (config section "Receipts"); every key is optional.</summary>
public class ReceiptOptions
{
  /// <summary>How long a receipt waits for its bank charge before it is given up on.</summary>
  public int WaitDays { get; set; } = 14;

  public bool RequirePremium { get; set; } = true;

  /// <summary>Receipts a user can send in a day (each one is a Gemini call).</summary>
  public int MaxPerDay { get; set; } = 40;

  public static ReceiptOptions From(IConfiguration configuration)
  {
    var section = configuration.GetSection("Receipts");
    return new ReceiptOptions
    {
      WaitDays = int.TryParse(section["WaitDays"], out var d) && d > 0 ? d : 14,
      RequirePremium = !bool.TryParse(section["RequirePremium"], out var p) || p,
      MaxPerDay = int.TryParse(section["MaxPerDay"], out var m) && m > 0 ? m : 40,
    };
  }
}
