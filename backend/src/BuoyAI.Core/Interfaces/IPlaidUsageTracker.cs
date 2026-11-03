using BuoyAI.Core.DTOs.Plaid;

namespace BuoyAI.Core.Interfaces;

public interface IPlaidUsageTracker
{
    Task<bool> CanMakeBalanceRequest(string itemId);
    Task<bool> CanMakeRefreshRequest(string itemId);
    Task RecordBalanceRequest(string itemId);
    Task RecordRefreshRequest(string itemId);
    Task<PlaidUsageStats> GetUsageStats(string itemId);
}
