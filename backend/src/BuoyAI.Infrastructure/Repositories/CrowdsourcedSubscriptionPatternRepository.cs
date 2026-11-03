using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class CrowdsourcedSubscriptionPatternRepository : ICrowdsourcedSubscriptionPatternRepository
{
    private readonly BuoyAIDbContext _context;

    public CrowdsourcedSubscriptionPatternRepository(BuoyAIDbContext context)
    {
        _context = context;
    }

    public async Task<CrowdsourcedSubscriptionPattern?> GetByMerchantNameAsync(string normalizedMerchantName)
    {
        var normalized = normalizedMerchantName.ToLowerInvariant().Trim();
        return await _context.CrowdsourcedSubscriptionPatterns
            .FirstOrDefaultAsync(p => p.NormalizedMerchantName == normalized);
    }

    public async Task<IReadOnlyDictionary<string, CrowdsourcedSubscriptionPattern>> GetByMerchantNamesAsync(
        IEnumerable<string> normalizedMerchantNames)
    {
        var normalized = normalizedMerchantNames
            .Select(n => n.ToLowerInvariant().Trim())
            .Distinct()
            .ToList();

        if (normalized.Count == 0)
            return new Dictionary<string, CrowdsourcedSubscriptionPattern>();

        var rows = await _context.CrowdsourcedSubscriptionPatterns
            .Where(p => normalized.Contains(p.NormalizedMerchantName))
            .ToListAsync();

        // FirstOrDefault semantics (mirrors GetByMerchantNameAsync) if duplicates ever exist.
        return rows
            .GroupBy(p => p.NormalizedMerchantName)
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>
    /// Creates or updates a crowdsourced pattern based on user confirmation/dismissal.
    /// This is the core method for building the anonymized pattern database.
    /// </summary>
    public async Task<CrowdsourcedSubscriptionPattern> CreateOrUpdateAsync(
        string normalizedMerchantName, 
        decimal amount, 
        int frequency, 
        bool isConfirmation)
    {
        var normalized = normalizedMerchantName.ToLowerInvariant().Trim();
        var existing = await GetByMerchantNameAsync(normalized);

        if (existing == null)
        {
            // Create new pattern
            var pattern = new CrowdsourcedSubscriptionPattern
            {
                NormalizedMerchantName = normalized,
                TypicalAmountMin = amount,
                TypicalAmountMax = amount,
                TypicalFrequency = frequency,
                ConfirmationCount = isConfirmation ? 1 : 0,
                DismissalCount = isConfirmation ? 0 : 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            
            pattern.ConfidenceBoost = pattern.CalculateConfidenceBoost();
            
            _context.CrowdsourcedSubscriptionPatterns.Add(pattern);
            await _context.SaveChangesAsync();
            return pattern;
        }
        else
        {
            // Update existing pattern
            if (isConfirmation)
            {
                existing.ConfirmationCount++;
            }
            else
            {
                existing.DismissalCount++;
            }
            
            // Update amount range
            if (amount < existing.TypicalAmountMin)
                existing.TypicalAmountMin = amount;
            if (amount > existing.TypicalAmountMax)
                existing.TypicalAmountMax = amount;
            
            existing.UpdatedAt = DateTime.UtcNow;
            existing.ConfidenceBoost = existing.CalculateConfidenceBoost();
            
            _context.CrowdsourcedSubscriptionPatterns.Update(existing);
            await _context.SaveChangesAsync();
            return existing;
        }
    }

    public async Task<IEnumerable<CrowdsourcedSubscriptionPattern>> GetTopPatternsAsync(int count = 100)
    {
        return await _context.CrowdsourcedSubscriptionPatterns
            .Where(p => p.ConfirmationCount > 0)
            .OrderByDescending(p => p.ConfirmationCount)
            .Take(count)
            .ToListAsync();
    }

    /// <summary>
    /// Recalculates confidence boosts for all patterns. 
    /// Should be run periodically as part of maintenance.
    /// </summary>
    public async Task RecalculateConfidenceBoostsAsync()
    {
        var patterns = await _context.CrowdsourcedSubscriptionPatterns.ToListAsync();
        
        foreach (var pattern in patterns)
        {
            var oldBoost = pattern.ConfidenceBoost;
            var newBoost = pattern.CalculateConfidenceBoost();
            
            if (Math.Abs(oldBoost - newBoost) > 0.001)
            {
                pattern.ConfidenceBoost = newBoost;
                pattern.UpdatedAt = DateTime.UtcNow;
            }
        }
        
        await _context.SaveChangesAsync();
    }
}
