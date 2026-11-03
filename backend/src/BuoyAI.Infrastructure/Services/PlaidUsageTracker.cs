using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs.Plaid;

namespace BuoyAI.Infrastructure.Services;

public class PlaidUsageTracker : IPlaidUsageTracker
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<PlaidUsageTracker> _logger;
    private readonly IConfiguration _configuration;
    
    // Daily limits to prevent cost explosions
    private readonly int _maxBalanceRequestsPerItemPerDay;
    private readonly int _maxRefreshRequestsPerItemPerDay;

    public PlaidUsageTracker(
        IMemoryCache cache, 
        ILogger<PlaidUsageTracker> logger,
        IConfiguration configuration)
    {
        _cache = cache;
        _logger = logger;
        _configuration = configuration;
        
        // Load limits from configuration with safe defaults
        _maxBalanceRequestsPerItemPerDay = _configuration.GetValue<int>("Plaid:RateLimits:BalanceRequestsPerDay", 5);
        _maxRefreshRequestsPerItemPerDay = _configuration.GetValue<int>("Plaid:RateLimits:RefreshRequestsPerDay", 3);
    }

    public Task<bool> CanMakeBalanceRequest(string itemId)
    {
        var key = $"balance_{itemId}_{DateTime.UtcNow:yyyy-MM-dd}";
        var currentCount = _cache.Get<int>(key);
        
        if (currentCount >= _maxBalanceRequestsPerItemPerDay)
        {
            _logger.LogWarning("Balance request limit exceeded for item {ItemId}. Current: {Current}, Max: {Max}",
                itemId, currentCount, _maxBalanceRequestsPerItemPerDay);
            return Task.FromResult(false);
        }
        
        return Task.FromResult(true);
    }

    public Task<bool> CanMakeRefreshRequest(string itemId)
    {
        var key = $"refresh_{itemId}_{DateTime.UtcNow:yyyy-MM-dd}";
        var currentCount = _cache.Get<int>(key);
        
        if (currentCount >= _maxRefreshRequestsPerItemPerDay)
        {
            _logger.LogWarning("Refresh request limit exceeded for item {ItemId}. Current: {Current}, Max: {Max}",
                itemId, currentCount, _maxRefreshRequestsPerItemPerDay);
            return Task.FromResult(false);
        }
        
        return Task.FromResult(true);
    }

    public Task RecordBalanceRequest(string itemId)
    {
        var key = $"balance_{itemId}_{DateTime.UtcNow:yyyy-MM-dd}";
        var currentCount = _cache.Get<int>(key);
        _cache.Set(key, currentCount + 1, TimeSpan.FromDays(1));
        
        _logger.LogInformation("Balance request recorded for item {ItemId}. Count: {Count}/{Max}",
            itemId, currentCount + 1, _maxBalanceRequestsPerItemPerDay);
            
        return Task.CompletedTask;
    }

    public Task RecordRefreshRequest(string itemId)
    {
        var key = $"refresh_{itemId}_{DateTime.UtcNow:yyyy-MM-dd}";
        var currentCount = _cache.Get<int>(key);
        _cache.Set(key, currentCount + 1, TimeSpan.FromDays(1));
        
        _logger.LogInformation("Refresh request recorded for item {ItemId}. Count: {Count}/{Max}",
            itemId, currentCount + 1, _maxRefreshRequestsPerItemPerDay);
            
        return Task.CompletedTask;
    }

    public Task<PlaidUsageStats> GetUsageStats(string itemId)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var balanceKey = $"balance_{itemId}_{today}";
        var refreshKey = $"refresh_{itemId}_{today}";
        
        var stats = new PlaidUsageStats
        {
            ItemId = itemId,
            Date = DateTime.UtcNow.Date,
            BalanceRequests = _cache.Get<int>(balanceKey),
            RefreshRequests = _cache.Get<int>(refreshKey),
            MaxBalanceRequests = _maxBalanceRequestsPerItemPerDay,
            MaxRefreshRequests = _maxRefreshRequestsPerItemPerDay
        };
        
        return Task.FromResult(stats);
    }
}
