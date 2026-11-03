using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class DismissedSubscriptionPatternRepository : IDismissedSubscriptionPatternRepository
{
    private readonly BuoyAIDbContext _context;

    public DismissedSubscriptionPatternRepository(BuoyAIDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<DismissedSubscriptionPattern>> GetByUserIdAsync(int userId)
    {
        return await _context.DismissedSubscriptionPatterns
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.DismissedAt)
            .ToListAsync();
    }

    public async Task<DismissedSubscriptionPattern?> GetByMerchantNameAsync(int userId, string merchantName)
    {
        var normalizedName = merchantName.ToLowerInvariant().Trim();
        return await _context.DismissedSubscriptionPatterns
            .FirstOrDefaultAsync(d => d.UserId == userId && 
                                      d.MerchantName == normalizedName);
    }

    public async Task<bool> IsPatternDismissedAsync(int userId, string merchantName, decimal? amount = null, int skipDays = 60)
    {
        var normalizedName = merchantName.ToLowerInvariant().Trim();
        var cutoffDate = DateTime.UtcNow.AddDays(-skipDays);
        
        var query = _context.DismissedSubscriptionPatterns
            .Where(d => d.UserId == userId && 
                        d.MerchantName == normalizedName &&
                        (d.IsPermanent || d.DismissedAt >= cutoffDate)); // Permanent dismissals ignore date cutoff
        
        // If amount is provided, check if it falls within any dismissed range
        if (amount.HasValue)
        {
            query = query.Where(d => 
                !d.AmountMin.HasValue || // No amount constraint, dismiss all from this merchant
                (d.AmountMin <= amount && d.AmountMax >= amount)); // Amount falls in dismissed range
        }
        
        return await query.AnyAsync();
    }

    public async Task<IReadOnlyList<DismissedSubscriptionPattern>> GetActiveDismissedAsync(int userId, int skipDays = 60)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-skipDays);
        return await _context.DismissedSubscriptionPatterns
            .Where(d => d.UserId == userId &&
                        (d.IsPermanent || d.DismissedAt >= cutoffDate)) // Permanent dismissals ignore date cutoff
            .ToListAsync();
    }

    public async Task<DismissedSubscriptionPattern> CreateAsync(DismissedSubscriptionPattern pattern)
    {
        pattern.MerchantName = pattern.MerchantName.ToLowerInvariant().Trim();
        pattern.DismissedAt = DateTime.UtcNow;
        
        _context.DismissedSubscriptionPatterns.Add(pattern);
        await _context.SaveChangesAsync();
        return pattern;
    }

    public async Task DeleteAsync(int id)
    {
        var pattern = await _context.DismissedSubscriptionPatterns.FindAsync(id);
        if (pattern != null)
        {
            _context.DismissedSubscriptionPatterns.Remove(pattern);
            await _context.SaveChangesAsync();
        }
    }

    public async Task DeleteByUserIdAsync(int userId)
    {
        var patterns = await _context.DismissedSubscriptionPatterns
            .Where(d => d.UserId == userId)
            .ToListAsync();
        
        _context.DismissedSubscriptionPatterns.RemoveRange(patterns);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> IsPermanentlyDismissedAsync(int userId, string merchantName)
    {
        var normalizedName = merchantName.ToLowerInvariant().Trim();
        return await _context.DismissedSubscriptionPatterns
            .AnyAsync(d => d.UserId == userId && d.MerchantName == normalizedName && d.IsPermanent);
    }
}
