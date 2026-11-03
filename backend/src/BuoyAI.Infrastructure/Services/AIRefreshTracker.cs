using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Entities;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

public class AIRefreshTracker : IAIRefreshTracker
{
    private readonly BuoyAIDbContext _context;
    private readonly ILogger<AIRefreshTracker> _logger;
    

    public AIRefreshTracker(BuoyAIDbContext context, ILogger<AIRefreshTracker> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<bool> CanRefreshAsync(int userId, string context, SubscriptionTier tier)
    {
        try
        {
            var today = DateTime.UtcNow.Date;
            var dailyLimit = AiLimits.Default.For(tier).DailyRefreshes;
            
            var usage = await _context.AIRefreshUsages
                .FirstOrDefaultAsync(x => x.UserId == userId && x.Context == context && x.Date == today);
                
            var currentCount = usage?.RefreshCount ?? 0;
            var canRefresh = currentCount < dailyLimit;
            
            _logger.LogInformation("Refresh check for user {UserId}, context {Context}, tier {Tier}: {CurrentCount}/{DailyLimit}, canRefresh: {CanRefresh}", 
                userId, context, tier, currentCount, dailyLimit, canRefresh);
                
            return canRefresh;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking refresh availability for user {UserId}, context {Context}", userId, context);
            return false;
        }
    }

    public async Task<int> GetDailyRefreshCountAsync(int userId, string context)
    {
        try
        {
            var today = DateTime.UtcNow.Date;
            
            var usage = await _context.AIRefreshUsages
                .FirstOrDefaultAsync(x => x.UserId == userId && x.Context == context && x.Date == today);
                
            return usage?.RefreshCount ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting daily refresh count for user {UserId}, context {Context}", userId, context);
            return 0;
        }
    }

    public async Task RecordRefreshAsync(int userId, string context)
    {
        try
        {
            var today = DateTime.UtcNow.Date;
            var now = DateTime.UtcNow;
            
            var usage = await _context.AIRefreshUsages
                .FirstOrDefaultAsync(x => x.UserId == userId && x.Context == context && x.Date == today);
                
            if (usage == null)
            {
                usage = new AIRefreshUsage
                {
                    UserId = userId,
                    Context = context,
                    Date = today,
                    RefreshCount = 1,
                    LastRefreshAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _context.AIRefreshUsages.Add(usage);
            }
            else
            {
                usage.RefreshCount++;
                usage.LastRefreshAt = now;
                usage.UpdatedAt = now;
            }
            
            await _context.SaveChangesAsync();
            
            _logger.LogInformation("Recorded refresh for user {UserId}, context {Context}. New count: {RefreshCount}", 
                userId, context, usage.RefreshCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording refresh for user {UserId}, context {Context}", userId, context);
            throw;
        }
    }

    public async Task<(int UsedToday, int DailyLimit)> GetRefreshStatsAsync(int userId, string context, SubscriptionTier tier)
    {
        try
        {
            var usedToday = await GetDailyRefreshCountAsync(userId, context);
            var dailyLimit = AiLimits.Default.For(tier).DailyRefreshes;
            
            return (usedToday, dailyLimit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting refresh stats for user {UserId}, context {Context}", userId, context);
            return (0, AiLimits.Default.For(tier).DailyRefreshes);
        }
    }
}
