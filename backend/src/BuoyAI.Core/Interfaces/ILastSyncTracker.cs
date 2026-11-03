namespace BuoyAI.Core.Interfaces;

public interface ILastSyncTracker
{
    Task<DateTime?> GetLastSyncTime(int userId);
    Task SetLastSyncTime(int userId, DateTime syncTime);
    Task<bool> IsDataStale(int userId, TimeSpan maxAge);
}
