using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Hosted-only: when the local projection says "no tier", ask Stripe once (debounced inside
/// the sync service) before the answer locks someone out.
///
/// Resolves the sync service lazily. It depends on Stripe, whose service throws at
/// construction without keys, and the entitlement service must stay buildable without them.
/// </summary>
public class StripeTierRefreshHook : ITierRefreshHook
{
    private readonly IServiceProvider _services;
    private readonly ILogger<StripeTierRefreshHook> _logger;

    public StripeTierRefreshHook(IServiceProvider services, ILogger<StripeTierRefreshHook> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task<bool> RefreshAsync(int userId, string reason)
    {
        ISubscriptionSyncService sync;
        try
        {
            sync = _services.GetRequiredService<ISubscriptionSyncService>();
        }
        catch (Exception ex)
        {
            // No Stripe keys (self-hosted): there is nothing to re-check, so the denial stands.
            _logger.LogDebug(ex, "Stripe sync unavailable; tier denial for user {UserId} stands", userId);
            return Task.FromResult(false);
        }

        return sync.RefreshFromStripeAsync(userId, reason);
    }
}
