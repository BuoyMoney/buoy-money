namespace BuoyAI.Core.DTOs.Plaid;

public class PlaidUsageStats
{
    public string ItemId { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public int BalanceRequests { get; set; }
    public int RefreshRequests { get; set; }
    public int MaxBalanceRequests { get; set; }
    public int MaxRefreshRequests { get; set; }
    
    public bool IsNearBalanceLimit => BalanceRequests >= (MaxBalanceRequests * 0.8);
    public bool IsNearRefreshLimit => RefreshRequests >= (MaxRefreshRequests * 0.8);
}
