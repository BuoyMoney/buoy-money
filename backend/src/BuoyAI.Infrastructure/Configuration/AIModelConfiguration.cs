using BuoyAI.Core.Enums;
using Microsoft.Extensions.Configuration;

namespace BuoyAI.Infrastructure.Configuration;

/// <summary>
/// Centralized configuration for AI model selection based on subscription tier.
/// Eliminates duplication across AIService, AICategorizationService, etc.
/// </summary>
public class AIModelConfiguration
{
    private readonly Dictionary<SubscriptionTier, string[]> _modelTiers;
    
    /// <summary>
    /// Default models if not configured in appsettings
    /// </summary>
    // 2026-09: Standard -> 2.5-flash (unchanged); Premium/Admin -> 3.8-flash (GA 2026-09,
    // same intro pricing as 3.7) with a 2.5-flash fallback. gemini-2.0-flash* are
    // decommissioned (generateContent 404s).
    // Standard (insights only, structured JSON): 3.5-flash-lite is 2.5-flash's exact price two
    // generations newer; 2.5-flash stays as the known-good net. Premium/Admin: 3.8-flash with a
    // 3.7-flash fallback so a failover is behaviorally invisible (same price, same tool-calling
    // conventions) instead of a quality cliff. Intro pricing on 3.6-3.8 doubles 2027-01-01.
    private const string DefaultFreeModel = "gemini-3.5-flash-lite";
    private const string DefaultFreeFallback = "gemini-2.5-flash";
    private const string DefaultProModel = "gemini-3.8-flash";
    private const string DefaultProFallback = "gemini-3.7-flash";

    public AIModelConfiguration(IConfiguration configuration)
    {
        var geminiSettings = configuration.GetSection("Gemini");

        // Load model tiers from configuration. The appsettings files use
        // Gemini:Models:{Standard|Premium|Admin} — read those first. The old
        // {Free|Pro} keys are kept as a fallback because deploy-time env-var
        // overrides may still use them; before this fix the code read ONLY the
        // old names while every appsettings file used the new ones, so the
        // JSON overrides were silently dead and defaults always won.
        string Model(string primary, string legacy, string fallback) =>
            geminiSettings[primary] ?? geminiSettings[legacy] ?? fallback;

        _modelTiers = new Dictionary<SubscriptionTier, string[]>
        {
            [SubscriptionTier.Standard] = new[]
            {
                Model("Models:Standard:Primary", "Models:Free:Primary", DefaultFreeModel),
                Model("Models:Standard:Fallback", "Models:Free:Fallback", DefaultFreeFallback)
            },
            [SubscriptionTier.Premium] = new[]
            {
                Model("Models:Premium:Primary", "Models:Pro:Primary", DefaultProModel),
                Model("Models:Premium:Secondary", "Models:Pro:Secondary", DefaultProFallback)
            },
            [SubscriptionTier.Admin] = new[]
            {
                Model("Models:Admin:Primary", "Models:Pro:Primary", DefaultProModel),
                Model("Models:Admin:Secondary", "Models:Pro:Secondary", DefaultProFallback)
            }
        };
    }

    /// <summary>
    /// Gets the primary and fallback models for a given subscription tier.
    /// </summary>
    /// <param name="tier">The user's subscription tier</param>
    /// <returns>Array of model names [primary, fallback]</returns>
    public string[] GetModelsForTier(SubscriptionTier tier)
    {
        return _modelTiers.TryGetValue(tier.ForLimits(), out var models) 
            ? models 
            : _modelTiers[SubscriptionTier.Standard];
    }

    /// <summary>
    /// Gets the primary model for a given subscription tier.
    /// </summary>
    public string GetPrimaryModel(SubscriptionTier tier)
    {
        return GetModelsForTier(tier)[0];
    }

    /// <summary>
    /// Gets the fallback model for a given subscription tier.
    /// </summary>
    public string GetFallbackModel(SubscriptionTier tier)
    {
        var models = GetModelsForTier(tier);
        return models.Length > 1 ? models[1] : models[0];
    }
}
