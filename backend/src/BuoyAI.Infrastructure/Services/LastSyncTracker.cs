using Microsoft.Extensions.Caching.Memory;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services;

public class LastSyncTracker : ILastSyncTracker
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromHours(24);

    public LastSyncTracker(IMemoryCache cache)
    {
        _cache = cache;
    }

    public async Task<DateTime?> GetLastSyncTime(int userId)
    {
        return await Task.FromResult(_cache.Get<DateTime?>($"last_sync_{userId}"));
    }

    public async Task SetLastSyncTime(int userId, DateTime syncTime)
    {
        _cache.Set($"last_sync_{userId}", syncTime, _cacheExpiry);
        await Task.CompletedTask;
    }

    public async Task<bool> IsDataStale(int userId, TimeSpan maxAge)
    {
        var lastSync = await GetLastSyncTime(userId);
        if (!lastSync.HasValue)
            return true;

        return DateTime.UtcNow - lastSync.Value > maxAge;
    }
}
