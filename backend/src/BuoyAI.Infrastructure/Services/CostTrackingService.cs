
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services.Ai;

namespace BuoyAI.Infrastructure.Services;

public class CostTrackingService : ICostTrackingService
{
    private readonly ILogger<CostTrackingService> _logger;
    private readonly IMemoryCache _cache;
    private readonly IAIUsageTracker _usageTracker;
    private readonly IEntitlementService _entitlements;
    
    public CostTrackingService(
        ILogger<CostTrackingService> logger,
        IMemoryCache cache,
        IAIUsageTracker usageTracker,
        IEntitlementService entitlements)
    {
        _logger = logger;
        _cache = cache;
        _usageTracker = usageTracker;
        _entitlements = entitlements;
    }

    public async Task<decimal> GetUserDailyCostAsync(int userId)
    {
        try
        {
            var usage = await _usageTracker.GetDailyUsageAsync(userId);
            return usage.CostToday;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting daily cost for user {UserId}", userId);
            return 0;
        }
    }

    public async Task<bool> CanUserMakeRequestAsync(int userId, int estimatedTokens)
    {
        try
        {
            // Get user's actual subscription tier
            var tier = await _entitlements.GetEffectiveTierAsync(userId) ?? SubscriptionTier.Standard;
            
            var currentCost = await GetUserDailyCostAsync(userId);
            var estimatedCost = CalculateEstimatedCost(estimatedTokens);
            
            var limit = _entitlements.GetAiLimits(tier).DailyCost;
            
            if (currentCost + estimatedCost > limit)
            {
                _logger.LogWarning("User {UserId} (tier: {Tier}) would exceed daily cost limit: Current={Current}, Estimated={Estimated}, Limit={Limit}", 
                    userId, tier, currentCost, estimatedCost, limit);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking cost limits for user {UserId}", userId);
            return false; // Fail closed
        }
    }

    public async Task RecordAPIUsageAsync(int userId, string model, int inputTokens, int outputTokens, decimal cost)
    {
        try
        {
            await _usageTracker.RecordUsageAsync(userId, model, inputTokens + outputTokens, cost);
            
            _logger.LogInformation("Recorded API usage for user {UserId}: Model={Model}, InputTokens={InputTokens}, OutputTokens={OutputTokens}, Cost={Cost}", 
                userId, model, inputTokens, outputTokens, cost);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording API usage for user {UserId}", userId);
        }
    }

    public async Task<AIUsageStats> GetUsageStatsAsync(int userId)
    {
        return await _usageTracker.GetDailyUsageAsync(userId);
    }

    public decimal CalculateCost(string model, int inputTokens, int outputTokens) =>
        CalculateActualCost(model, inputTokens, outputTokens);

    // Used only to ask "would this request tip the daily cap". The model isn't known yet, so price it at the
    // dearest everyday Gemini rate (the same figure the cap check has always been conservative about).
    private const string EstimateModel = "gemini-3.5-flash";

    private decimal CalculateEstimatedCost(int tokens)
    {
        // Roughly 80% input, 20% output tokens.
        var inputTokens = (int)(tokens * 0.8);
        return AiPricing.Cost(EstimateModel, inputTokens, tokens - inputTokens);
    }

    private decimal CalculateActualCost(string model, int inputTokens, int outputTokens) =>
        AiPricing.Cost(model, inputTokens, outputTokens, _logger);
}
