using BuoyAI.Core.Entities;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

public interface IAIRefreshTracker
{
    Task<bool> CanRefreshAsync(int userId, string context, SubscriptionTier tier);
    Task<int> GetDailyRefreshCountAsync(int userId, string context);
    Task RecordRefreshAsync(int userId, string context);
    Task<(int UsedToday, int DailyLimit)> GetRefreshStatsAsync(int userId, string context, SubscriptionTier tier);
}
