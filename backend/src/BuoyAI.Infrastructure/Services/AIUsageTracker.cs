using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services.Ai;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

public class AIUsageTracker : IAIUsageTracker
{
    private readonly ILogger<AIUsageTracker> _logger;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly BuoyAIDbContext _dbContext;
    private readonly BuoyAiMetrics _metrics;

    // The one source of per-tier caps; daily request counts honour Gemini:RateLimiting:*.
    private readonly AiLimits _limits;

    // Per-tier caps are the hosted service's. A self-hosted server runs on its own model or key (or on BuoyAI Cloud, which
    // enforces its own allowance), so there the tracker only keeps statistics and never blocks.
    private readonly bool _enforceLimits;

    // When the meter can't be read, hosted refuses rather than letting spend through unchecked.
    // Ai:Metering:FailClosed overrides the default (true on hosted).
    private readonly bool _failClosed;

    // Whether the monthly cost cap (and the fail-closed answer) also gates requests on hosted. On by default
    // (owner decision 2026-10-12); set Ai:Metering:EnforceMonthlyCap=false to go back to warning only.
    private readonly bool _enforceMonthlyCap;

    public AIUsageTracker(
        ILogger<AIUsageTracker> logger,
        IMemoryCache cache,
        IConfiguration configuration,
        BuoyAIDbContext dbContext,
        BuoyAiMetrics metrics,
        EditionInfo? edition = null)
    {
        _logger = logger;
        _cache = cache;
        _configuration = configuration;
        _dbContext = dbContext;
        _metrics = metrics;

        _limits = AiLimitsConfiguration.Load(_configuration);
        _enforceLimits = edition?.IsSelfHost != true;
        _failClosed = _enforceLimits && configuration.GetValue("Ai:Metering:FailClosed", true);
        _enforceMonthlyCap = _enforceLimits && configuration.GetValue("Ai:Metering:EnforceMonthlyCap", true);
    }

    public async Task<bool> CanMakeRequestAsync(int userId, SubscriptionTier tier)
    {
        if (!_enforceLimits) return true;
        try
        {
            var limits = _limits.For(tier);

            // Check hourly limit (using cache for quick rate limiting)
            var hourlyKey = $"ai_usage_hourly_{userId}_{DateTime.UtcNow:yyyyMMddHH}";
            var hourlyCount = _cache.Get<int>(hourlyKey);
            if (hourlyCount >= limits.HourlyRequests)
            {
                _logger.LogWarning("User {UserId} exceeded hourly AI limit: {Count}/{Limit}",
                    userId, hourlyCount, limits.HourlyRequests);
                return false;
            }

            // Check daily limit
            var dailyKey = $"ai_usage_daily_{userId}_{DateTime.UtcNow:yyyyMMdd}";
            var dailyCount = _cache.Get<int>(dailyKey);
            if (dailyCount >= limits.DailyRequests)
            {
                _logger.LogWarning("User {UserId} exceeded daily AI limit: {Count}/{Limit}",
                    userId, dailyCount, limits.DailyRequests);
                return false;
            }

            // With Ai:Metering:EnforceMonthlyCap on, the monthly cost cap (and the fail-closed answer when the
            // meter is unreadable) gates the request too. On by default for hosted (owner decision 2026-10-12).
            if (!_enforceMonthlyCap) return true;
            var (canProceed, _, _) = await CheckMonthlyLimitAsync(userId, tier);
            return canProceed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking AI usage limits for user {UserId}", userId);
            return false; // Fail closed
        }
    }

    public async Task<(bool CanProceed, string? WarningLevel, decimal UsagePercent)> CheckMonthlyLimitAsync(int userId, SubscriptionTier tier)
    {
        try
        {
            var monthlyStats = await GetMonthlyUsageAsync(userId, throwOnError: true);
            var limit = _limits.For(tier).MonthlyCost;
            var usagePercent = limit > 0 ? (monthlyStats.CostThisMonth / limit) * 100 : 0;

            // Self-host: numbers only, no cap.
            if (!_enforceLimits) return (true, usagePercent >= 80 ? "warning" : null, usagePercent);

            if (usagePercent >= 100)
            {
                _logger.LogWarning("User {UserId} has reached monthly AI cost limit: ${Cost}/${Limit} ({Percent:F1}%)",
                    userId, monthlyStats.CostThisMonth, limit, usagePercent);
                return (false, "blocked", usagePercent);
            }

            if (usagePercent >= 80)
            {
                _logger.LogInformation("User {UserId} approaching monthly AI cost limit: ${Cost}/${Limit} ({Percent:F1}%)",
                    userId, monthlyStats.CostThisMonth, limit, usagePercent);
                return (true, "warning", usagePercent);
            }

            return (true, null, usagePercent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking monthly limit for user {UserId}", userId);
            // Fail closed on hosted: an unreadable meter is not permission to spend. (This used to let the request through.)
            return _failClosed ? (false, "unavailable", 0) : (true, null, 0);
        }
    }

    public async Task RecordUsageAsync(int userId, string modelUsed, int tokensUsed, decimal cost, string requestContext = "chat")
    {
        try
        {
            var now = DateTime.UtcNow;

            // Update cache for quick rate limiting (hourly)
            var hourlyKey = $"ai_usage_hourly_{userId}_{now:yyyyMMddHH}";
            var hourlyCount = _cache.Get<int>(hourlyKey);
            _cache.Set(hourlyKey, hourlyCount + 1, TimeSpan.FromHours(2));

            // Update cache for quick rate limiting (daily)
            var dailyKey = $"ai_usage_daily_{userId}_{now:yyyyMMdd}";
            var dailyCount = _cache.Get<int>(dailyKey);
            _cache.Set(dailyKey, dailyCount + 1, TimeSpan.FromDays(2));

            // Persist to database for monthly tracking
            var record = new AIUsageRecord
            {
                UserId = userId,
                Model = modelUsed,
                InputTokens = (int)(tokensUsed * 0.7), // Estimate 70% input
                OutputTokens = (int)(tokensUsed * 0.3), // Estimate 30% output
                TokensEstimated = true, // one total split 70/30 is a guess; callers that know the split use RecordCallAsync
                EstimatedCost = cost,
                Timestamp = now,
                RequestContext = requestContext,
                CreatedAt = now
            };

            _dbContext.AIUsageRecords.Add(record);
            await _dbContext.SaveChangesAsync();

            // Emitted after the row is safely persisted so the metric can never claim
            // spend the database doesn't have. Feeds the cost-rate alert (see
            // BuoyAiMetrics.RecordAiUsage) that guards the prepaid Gemini balance.
            _metrics.RecordAiUsage(modelUsed, requestContext, tokensUsed, cost);

            _logger.LogInformation("Recorded AI usage for user {UserId}: Model={Model}, Tokens={Tokens}, Cost=${Cost:F4}",
                userId, modelUsed, tokensUsed, cost);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording AI usage for user {UserId}", userId);
            // Don't throw - we don't want to block the user if tracking fails
        }
    }

    public async Task RecordCallAsync(AiUsageEntry entry)
    {
        try
        {
            var now = DateTime.UtcNow;

            if (entry.CountsAgainstRequestLimits)
            {
                var hourlyKey = $"ai_usage_hourly_{entry.UserId}_{now:yyyyMMddHH}";
                _cache.Set(hourlyKey, _cache.Get<int>(hourlyKey) + 1, TimeSpan.FromHours(2));
                var dailyKey = $"ai_usage_daily_{entry.UserId}_{now:yyyyMMdd}";
                _cache.Set(dailyKey, _cache.Get<int>(dailyKey) + 1, TimeSpan.FromDays(2));
            }

            // The columns are 50 and 30 characters; OpenRouter ids and local tags can be longer.
            var model = entry.Model.Length > 50 ? entry.Model[..50] : entry.Model;
            var provider = entry.Provider is { Length: > 30 } ? entry.Provider[..30] : entry.Provider;
            _dbContext.AIUsageRecords.Add(new AIUsageRecord
            {
                UserId = entry.UserId,
                Model = model,
                Provider = provider,
                InputTokens = Math.Max(0, entry.InputTokens),
                OutputTokens = Math.Max(0, entry.OutputTokens),
                TokensEstimated = entry.TokensEstimated,
                EstimatedCost = entry.Cost,
                Timestamp = now,
                RequestContext = entry.RequestContext,
                CreatedAt = now
            });
            await _dbContext.SaveChangesAsync();

            if (entry.EmitMetrics)
                _metrics.RecordAiUsage(model, entry.RequestContext, (long)entry.InputTokens + entry.OutputTokens, entry.Cost);

            _logger.LogInformation(
                "Recorded AI usage for user {UserId}: Model={Model}, Context={Context}, In={In}, Out={Out}, Estimated={Estimated}, Cost=${Cost:F6}",
                entry.UserId, model, entry.RequestContext, entry.InputTokens, entry.OutputTokens, entry.TokensEstimated, entry.Cost);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording AI usage for user {UserId}", entry.UserId);
            // Don't throw: a bookkeeping failure must never fail the call that already succeeded.
        }
    }

    public async Task<AIUsageStats> GetDailyUsageAsync(int userId)
    {
        try
        {
            var today = DateTime.UtcNow.Date;

            // Get from database
            var todayRecords = await _dbContext.AIUsageRecords
                .Where(r => r.UserId == userId && r.Timestamp >= today)
                .ToListAsync();

            var dailyKey = $"ai_usage_daily_{userId}_{DateTime.UtcNow:yyyyMMdd}";
            var cachedCount = _cache.Get<int>(dailyKey);

            return new AIUsageStats
            {
                RequestsToday = Math.Max(todayRecords.Count, cachedCount),
                CostToday = todayRecords.Sum(r => r.EstimatedCost),
                LastRequestAt = todayRecords.OrderByDescending(r => r.Timestamp).FirstOrDefault()?.Timestamp ?? DateTime.MinValue
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting daily usage for user {UserId}", userId);
            return new AIUsageStats();
        }
    }

    public Task<AIUsageStats> GetMonthlyUsageAsync(int userId) => GetMonthlyUsageAsync(userId, throwOnError: false);

    // "Month" is the user's billing period (see AiBillingPeriod), not the calendar month.
    private async Task<AIUsageStats> GetMonthlyUsageAsync(int userId, bool throwOnError)
    {
        try
        {
            var period = await AiBillingPeriod.ForUserAsync(_dbContext, userId, DateTime.UtcNow);

            var monthlyRecords = await _dbContext.AIUsageRecords
                .Where(r => r.UserId == userId && r.Timestamp >= period.Start && r.Timestamp < period.End)
                .ToListAsync();

            var dailyStats = await GetDailyUsageAsync(userId);

            return new AIUsageStats
            {
                RequestsThisMonth = monthlyRecords.Count,
                CostThisMonth = monthlyRecords.Sum(r => r.EstimatedCost),
                RequestsToday = dailyStats.RequestsToday,
                CostToday = dailyStats.CostToday,
                LastRequestAt = dailyStats.LastRequestAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting monthly usage for user {UserId}", userId);
            if (throwOnError) throw;
            return new AIUsageStats();
        }
    }
}
