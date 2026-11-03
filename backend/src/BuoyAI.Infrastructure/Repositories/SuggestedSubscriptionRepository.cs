using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class SuggestedSubscriptionRepository : ISuggestedSubscriptionRepository
{
    private readonly BuoyAIDbContext _context;

    public SuggestedSubscriptionRepository(BuoyAIDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<SuggestedSubscription>> GetByUserIdAsync(int userId)
    {
        return await _context.SuggestedSubscriptions
            .Where(s => s.UserId == userId)
            .Include(s => s.CrowdsourcePattern)
            .OrderByDescending(s => s.DetectedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<SuggestedSubscription>> GetPendingByUserIdAsync(int userId)
    {
        var now = DateTime.UtcNow;

        // Un-snooze expired suggestions: flip Snoozed → Pending once the snooze window elapses.
        // Snoozed rows carry Status=Snoozed, so without this transition they would never satisfy
        // the Status==Pending filter below and would stay hidden forever (snooze == silent delete).
        await _context.SuggestedSubscriptions
            .Where(s => s.UserId == userId &&
                        s.Status == SuggestedSubscriptionStatus.Snoozed &&
                        s.SnoozeUntil != null && s.SnoozeUntil <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Status, SuggestedSubscriptionStatus.Pending)
                .SetProperty(s => s.SnoozeUntil, (DateTime?)null)
                .SetProperty(s => s.UpdatedAt, now));

        return await _context.SuggestedSubscriptions
            .Where(s => s.UserId == userId &&
                        s.Status == SuggestedSubscriptionStatus.Pending)
            .Include(s => s.CrowdsourcePattern)
            .OrderByDescending(s => s.Confidence)
            .ToListAsync();
    }

    public async Task<SuggestedSubscription?> GetByIdAsync(int id)
    {
        return await _context.SuggestedSubscriptions
            .Include(s => s.CrowdsourcePattern)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<SuggestedSubscription?> GetByMerchantAsync(int userId, string normalizedMerchantName)
    {
        var normalized = normalizedMerchantName.ToLowerInvariant().Trim();
        return await _context.SuggestedSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId && 
                                      s.NormalizedMerchantName == normalized);
    }

    public async Task<SuggestedSubscription> CreateAsync(SuggestedSubscription suggestion)
    {
        suggestion.NormalizedMerchantName = suggestion.NormalizedMerchantName.ToLowerInvariant().Trim();
        suggestion.DetectedAt = DateTime.UtcNow;
        suggestion.CreatedAt = DateTime.UtcNow;
        suggestion.UpdatedAt = DateTime.UtcNow;
        
        _context.SuggestedSubscriptions.Add(suggestion);
        await _context.SaveChangesAsync();
        return suggestion;
    }

    public async Task<SuggestedSubscription> UpdateAsync(SuggestedSubscription suggestion)
    {
        suggestion.UpdatedAt = DateTime.UtcNow;
        _context.SuggestedSubscriptions.Update(suggestion);
        await _context.SaveChangesAsync();
        return suggestion;
    }

    public async Task DeleteAsync(int id)
    {
        var suggestion = await _context.SuggestedSubscriptions.FindAsync(id);
        if (suggestion != null)
        {
            _context.SuggestedSubscriptions.Remove(suggestion);
            await _context.SaveChangesAsync();
        }
    }

    public async Task DeleteByUserIdAsync(int userId)
    {
        var suggestions = await _context.SuggestedSubscriptions
            .Where(s => s.UserId == userId)
            .ToListAsync();
        
        _context.SuggestedSubscriptions.RemoveRange(suggestions);
        await _context.SaveChangesAsync();
    }

    public async Task<int> GetSuggestionCountSinceAsync(int userId, DateTime since)
    {
        return await _context.SuggestedSubscriptions
            .CountAsync(s => s.UserId == userId && 
                            s.Status == SuggestedSubscriptionStatus.Pending &&
                            s.DetectedAt >= since);
    }

    public async Task<int> ExpireStaleSuggestionsAsync(TimeSpan maxAge)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - maxAge;
        var days = (int)maxAge.TotalDays;

        return await _context.SuggestedSubscriptions
            .Where(s => s.Status == SuggestedSubscriptionStatus.Pending && s.DetectedAt < cutoff)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Status, SuggestedSubscriptionStatus.Dismissed)
                .SetProperty(s => s.DismissReason, $"Auto-expired after {days} days without review")
                .SetProperty(s => s.ActionTakenAt, now)
                .SetProperty(s => s.UpdatedAt, now));
    }
}
