using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class AIInsightRepository : IAIInsightRepository
{
    private readonly BuoyAIDbContext _context;

    public AIInsightRepository(BuoyAIDbContext context)
    {
        _context = context;
    }

    public async Task<List<AIInsight>> GetInsightsForUserAsync(int userId, string context, int limit = 10, bool includesDismissed = false, bool includeArchived = false)
    {
        var query = _context.AIInsights
            .Where(i => i.UserId == userId && i.Context == context);

        if (!includesDismissed)
        {
            query = query.Where(i => !i.IsDismissed);
        }
        
        if (!includeArchived)
        {
            query = query.Where(i => !i.IsArchived);
        }

        return await query
            .OrderByDescending(i => i.GeneratedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<AIInsight>> GetRecentInsightsForUserAsync(int userId, string context, DateTime since, bool includesDismissed = false, bool includeArchived = false)
    {
        var query = _context.AIInsights
            .Where(i => i.UserId == userId && i.Context == context && i.GeneratedAt >= since);

        if (!includesDismissed)
        {
            query = query.Where(i => !i.IsDismissed);
        }
        
        if (!includeArchived)
        {
            query = query.Where(i => !i.IsArchived);
        }

        return await query
            .OrderByDescending(i => i.GeneratedAt)
            .ToListAsync();
    }

    public async Task<AIInsight?> GetInsightByIdAsync(int insightId)
    {
        return await _context.AIInsights.FindAsync(insightId);
    }

    public async Task<AIInsight> CreateInsightAsync(AIInsight insight)
    {
        insight.CreatedAt = DateTime.UtcNow;
        insight.UpdatedAt = DateTime.UtcNow;
        
        _context.AIInsights.Add(insight);
        await _context.SaveChangesAsync();
        return insight;
    }

    public async Task<AIInsight> UpdateInsightAsync(AIInsight insight)
    {
        insight.UpdatedAt = DateTime.UtcNow;
        
        _context.AIInsights.Update(insight);
        await _context.SaveChangesAsync();
        return insight;
    }

    public async Task<bool> DismissInsightAsync(int userId, string externalInsightId)
    {
        // The externalInsightId might be in format "context_insight_guid"
        // We need to find by the external ID pattern and userId
        var insight = await _context.AIInsights
            .FirstOrDefaultAsync(i => i.UserId == userId && 
                                    (i.Id.ToString() == externalInsightId || 
                                     externalInsightId.Contains(i.Id.ToString())));

        if (insight == null)
        {
            return false;
        }

        insight.IsDismissed = true;
        insight.DismissedAt = DateTime.UtcNow;
        insight.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ArchiveInsightsAsync(int userId, string context)
    {
        var activeInsights = await _context.AIInsights
            .Where(i => i.UserId == userId && i.Context == context && !i.IsArchived && !i.IsDismissed)
            .ToListAsync();

        if (activeInsights.Any())
        {
            foreach (var insight in activeInsights)
            {
                insight.IsArchived = true;
                insight.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            return true;
        }

        return false;
    }

    public async Task<bool> DeleteOldInsightsAsync(int userId, string context, DateTime before)
    {
        var oldInsights = await _context.AIInsights
            .Where(i => i.UserId == userId && i.Context == context && i.GeneratedAt < before)
            .ToListAsync();

        if (oldInsights.Any())
        {
            _context.AIInsights.RemoveRange(oldInsights);
            await _context.SaveChangesAsync();
            return true;
        }

        return false;
    }

    public async Task<int> GetInsightCountForUserAsync(int userId, string context, DateTime since)
    {
        return await _context.AIInsights
            .CountAsync(i => i.UserId == userId && i.Context == context && i.GeneratedAt >= since);
    }
}
