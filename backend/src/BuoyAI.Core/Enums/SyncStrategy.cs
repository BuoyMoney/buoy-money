namespace BuoyAI.Core.Enums;

/// <summary>
/// Defines the synchronization strategy for a user's financial data
/// </summary>
public enum SyncStrategy
{
    /// <summary>
    /// Premium users: Background sync every 30-60 minutes
    /// </summary>
    RealTime,
    
    /// <summary>
    /// Free users: Sync only on login when data is stale
    /// </summary>
    OnDemand,
    
    /// <summary>
    /// Inactive users: Weekly batch sync to prevent completely stale data
    /// </summary>
    Maintenance
}
