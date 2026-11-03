namespace BuoyAI.Core.Configuration;

/// <summary>
/// Founding-member spot cap. Bound to the "Founding" section. Only the first <see cref="DefaultSpotLimit"/>
/// paid founding reservations count as founding spots unless the owner changes SpotLimit.
/// </summary>
public class FoundingOptions
{
  public const string SectionName = "Founding";
  public const int DefaultSpotLimit = 150;

  /// <summary>Configured cap. 0, negative or missing means <see cref="DefaultSpotLimit"/>.</summary>
  public int SpotLimit { get; set; } = DefaultSpotLimit;

  public int EffectiveLimit => SpotLimit <= 0 ? DefaultSpotLimit : SpotLimit;
}
