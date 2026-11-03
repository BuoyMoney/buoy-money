using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces;

public interface IAIInsightRepository
{
    Task<List<AIInsight>> GetInsightsForUserAsync(int userId, string context, int limit = 10, bool includesDismissed = false, bool includeArchived = false);
    Task<List<AIInsight>> GetRecentInsightsForUserAsync(int userId, string context, DateTime since, bool includesDismissed = false, bool includeArchived = false);
    Task<AIInsight?> GetInsightByIdAsync(int insightId);
    Task<AIInsight> CreateInsightAsync(AIInsight insight);
    Task<AIInsight> UpdateInsightAsync(AIInsight insight);
    Task<bool> DismissInsightAsync(int userId, string externalInsightId);
    Task<bool> ArchiveInsightsAsync(int userId, string context);
    Task<bool> DeleteOldInsightsAsync(int userId, string context, DateTime before);
    Task<int> GetInsightCountForUserAsync(int userId, string context, DateTime since);
}
