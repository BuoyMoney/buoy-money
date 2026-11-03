using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuoyAI.UnitTests.Services;

/// <summary>Builds real <see cref="EntitlementService"/> instances for tests that need real tier rules.</summary>
internal static class TestEntitlements
{
    /// <summary>
    /// Over the given context and configuration. When <paramref name="sync"/> is given it is
    /// wired as the refresh hook, the same way the hosted build wires the Stripe sync.
    /// </summary>
    public static EntitlementService Create(
        BuoyAIDbContext context, IConfiguration configuration, ISubscriptionSyncService? sync = null)
    {
        var hooks = new List<ITierRefreshHook>();
        if (sync != null) hooks.Add(new SyncServiceHook(sync));
        return new EntitlementService(context, configuration, hooks, NullLogger<EntitlementService>.Instance);
    }

    /// <summary>For code that only asks row-level questions (no database read happens).</summary>
    public static EntitlementService For(IConfiguration configuration) =>
        Create(
            new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options),
            configuration);

    private sealed class SyncServiceHook : ITierRefreshHook
    {
        private readonly ISubscriptionSyncService _sync;
        public SyncServiceHook(ISubscriptionSyncService sync) => _sync = sync;
        public Task<bool> RefreshAsync(int userId, string reason) => _sync.RefreshFromStripeAsync(userId, reason);
    }
}
