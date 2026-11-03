using System.Text.Json;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;

namespace BuoyAI.Infrastructure.Services;

public interface IWrappedGarnishService
{
  /// <summary>
  /// Pick and phrase up to two bonus cards from the fact sheet. The model
  /// ONLY selects fact keys and writes short framing copy; every number the
  /// user sees comes verbatim from the fact sheet. Falls back to the
  /// deterministic surprise-ranked rotation on any failure.
  /// </summary>
  Task<List<WrappedBonusCardDto>> GenerateAsync(List<WrappedFactDto> facts);
}

public class WrappedGarnishService : IWrappedGarnishService
{
  private readonly IGeminiApiClient _gemini;
  private readonly AIModelConfiguration _models;
  private readonly ILogger<WrappedGarnishService> _logger;

  public WrappedGarnishService(
    IGeminiApiClient gemini,
    AIModelConfiguration models,
    ILogger<WrappedGarnishService> logger)
  {
    _gemini = gemini;
    _models = models;
    _logger = logger;
  }

  public async Task<List<WrappedBonusCardDto>> GenerateAsync(List<WrappedFactDto> facts)
  {
    if (facts.Count == 0) return new List<WrappedBonusCardDto>();

    try
    {
      var sheet = JsonSerializer.Serialize(facts.Select(f => new
      {
        key = f.Key,
        kicker = f.Kicker,
        headline = f.Headline,
        value = f.Value,
        detail = f.Detail,
      }));

      var prompt =
        "You are choosing 2 \"did you know\" cards for a personal-finance monthly recap story " +
        "(like Spotify Wrapped). Below is a fact sheet of pre-computed stats about ONE user's month. " +
        "Pick the TWO most surprising/delightful facts and write fresh framing copy for each.\n\n" +
        "STRICT RULES:\n" +
        "- Respond with ONLY a JSON array of exactly 2 objects: " +
        "[{\"factKey\":\"...\",\"kicker\":\"...\",\"headline\":\"...\"}]\n" +
        "- factKey must be a key from the fact sheet; the two keys must differ.\n" +
        "- kicker: 2-4 lowercase words, playful.\n" +
        "- headline: max 60 characters, warm and celebratory or wry, COMPLETES the card under the " +
        "big value (the value is rendered separately, do not repeat it).\n" +
        "- NEVER include any digits, amounts, or percentages in kicker or headline.\n" +
        "- No advice, no judgment, no shame, no exclamation overload.\n\n" +
        $"FACT SHEET:\n{sheet}";

      var model = _models.GetPrimaryModel(SubscriptionTier.Standard); // cheap tier is plenty
      var response = await _gemini.GenerateContentAsync(prompt, model);
      var cards = ParseAndValidate(response, facts);
      if (cards.Count > 0) return cards;
      _logger.LogWarning("Wrapped garnish response failed validation, using rotation");
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Wrapped garnish generation failed, using rotation");
    }

    return RotationPick(facts);
  }

  /// <summary>Deterministic fallback: the two most surprising facts as-is.</summary>
  public static List<WrappedBonusCardDto> RotationPick(List<WrappedFactDto> facts)
  {
    return facts
      .OrderByDescending(f => f.Surprise)
      .Take(2)
      .Select(f => new WrappedBonusCardDto
      {
        FactKey = f.Key,
        Kicker = f.Kicker,
        Headline = f.Headline,
        Value = f.Value,
        Detail = f.Detail,
        Source = "rotation",
      })
      .ToList();
  }

  internal static List<WrappedBonusCardDto> ParseAndValidate(
    string response, List<WrappedFactDto> facts)
  {
    var result = new List<WrappedBonusCardDto>();
    try
    {
      // Models love fencing JSON; strip any fence before parsing.
      var json = response.Trim();
      var start = json.IndexOf('[');
      var end = json.LastIndexOf(']');
      if (start < 0 || end <= start) return result;
      json = json[start..(end + 1)];

      var byKey = facts.ToDictionary(f => f.Key);
      using var doc = JsonDocument.Parse(json);
      foreach (var el in doc.RootElement.EnumerateArray())
      {
        var key = el.TryGetProperty("factKey", out var k) ? k.GetString() : null;
        var kicker = el.TryGetProperty("kicker", out var ki) ? ki.GetString() : null;
        var headline = el.TryGetProperty("headline", out var h) ? h.GetString() : null;

        // Hard validation: the fact must exist, copy must be present, short,
        // and digit-free (numbers only ever come from OUR fact sheet).
        if (key == null || !byKey.TryGetValue(key, out var fact)) continue;
        if (string.IsNullOrWhiteSpace(kicker) || string.IsNullOrWhiteSpace(headline)) continue;
        if (headline.Length > 90 || kicker.Length > 40) continue;
        if (headline.Any(char.IsDigit) || kicker.Any(char.IsDigit)) continue;
        if (result.Any(c => c.FactKey == key)) continue;

        result.Add(new WrappedBonusCardDto
        {
          FactKey = key,
          Kicker = kicker.Trim(),
          Headline = headline.Trim(),
          Value = fact.Value,
          Detail = fact.Detail,
          Source = "ai",
        });
        if (result.Count == 2) break;
      }
    }
    catch (JsonException)
    {
      return new List<WrappedBonusCardDto>();
    }
    return result;
  }
}
