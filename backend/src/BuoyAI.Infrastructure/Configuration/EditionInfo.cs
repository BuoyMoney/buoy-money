using Microsoft.Extensions.Configuration;

namespace BuoyAI.Infrastructure.Configuration;

public enum Edition
{
  /// <summary>The hosted service at buoy-ai.com: Stripe, RevenueCat, Plaid, marketing jobs. The default.</summary>
  Hosted,

  /// <summary>A server the owner runs for their own household: no billing, no growth jobs, no direct Plaid.</summary>
  SelfHost,
}

/// <summary>
/// Which edition this process is. Read once from <c>BuoyMoney:Edition</c> ("hosted" or "selfhost";
/// unset means hosted). One answer for every gate, so the hosted-only code paths are switched off
/// from a single place instead of by twenty ad hoc checks. A value that is set but unrecognised
/// stops the app at startup: guessing hosted on a typo would try to reach Stripe from a home server.
/// </summary>
public sealed class EditionInfo
{
  public const string ConfigKey = "BuoyMoney:Edition";

  public Edition Edition { get; }
  public bool IsSelfHost => Edition == Edition.SelfHost;
  public bool IsHosted => Edition == Edition.Hosted;

  public EditionInfo(Edition edition) => Edition = edition;

  /// <summary>Name used in the API and logs: "hosted" or "selfhost".</summary>
  public string Name => IsSelfHost ? "selfhost" : "hosted";

  public static EditionInfo From(IConfiguration configuration) => new(Parse(configuration[ConfigKey]));

  public static Edition Parse(string? value)
  {
    if (string.IsNullOrWhiteSpace(value)) return Edition.Hosted;
    return value.Trim().ToLowerInvariant() switch
    {
      "hosted" => Edition.Hosted,
      "selfhost" or "self-host" or "self_host" => Edition.SelfHost,
      _ => throw new InvalidOperationException(
        $"{ConfigKey} must be 'hosted' or 'selfhost' (got '{value}').")
    };
  }
}
