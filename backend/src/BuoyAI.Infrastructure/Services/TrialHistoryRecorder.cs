using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Shared trial-history reads/writes keyed by the one-way email hash. Centralizes the
/// "has this email ever had a trial?" check and the durable grant record so every place
/// that grants a trial (registration paths + card-upfront trial) records it consistently.
/// Static over a DbContext to avoid threading another injected dependency through services.
/// </summary>
internal static class TrialHistoryRecorder
{
    public static async Task<bool> HasUsedTrialAsync(BuoyAIDbContext context, string email)
    {
        var hash = TrialEmailHasher.Compute(email);
        return hash != null && await context.TrialHistories.AnyAsync(t => t.EmailHash == hash);
    }

    /// <summary>Returns the trial-history row for this email, or null if none.</summary>
    public static async Task<TrialHistory?> GetAsync(BuoyAIDbContext context, string email)
    {
        var hash = TrialEmailHasher.Compute(email);
        if (hash == null)
        {
            return null;
        }
        return await context.TrialHistories.FirstOrDefaultAsync(t => t.EmailHash == hash);
    }

    public static async Task RecordGrantAsync(
        BuoyAIDbContext context, string email, int userId, string source, DateTime? trialEndsAt = null)
    {
        var hash = TrialEmailHasher.Compute(email);
        if (hash == null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var existing = await context.TrialHistories.FirstOrDefaultAsync(t => t.EmailHash == hash);
        if (existing == null)
        {
            context.TrialHistories.Add(new TrialHistory
            {
                EmailHash = hash,
                FirstTrialStartedAt = now,
                OriginalTrialEndsAt = trialEndsAt,
                LastTrialGrantedAt = now,
                TrialGrantCount = 1,
                FirstUserId = userId,
                FirstSource = source,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            // Keep the ORIGINAL trial end date; only backfill it if it was never set.
            existing.OriginalTrialEndsAt ??= trialEndsAt;
            existing.TrialGrantCount += 1;
            existing.LastTrialGrantedAt = now;
            existing.UpdatedAt = now;
        }

        await context.SaveChangesAsync();
    }
}
