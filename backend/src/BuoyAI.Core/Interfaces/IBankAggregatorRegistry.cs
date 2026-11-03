using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Resolves the <see cref="IBankAggregator"/> for a given <see cref="BankProvider"/>. Backed by all
/// registered aggregators, so adding a provider is a single DI registration — nothing here changes.
/// </summary>
public interface IBankAggregatorRegistry
{
    /// <summary>The aggregator for <paramref name="provider"/>. Throws if none is registered.</summary>
    IBankAggregator Get(BankProvider provider);

    /// <summary>Non-throwing lookup.</summary>
    bool TryGet(BankProvider provider, out IBankAggregator? aggregator);

    /// <summary>
    /// Finds the aggregator that can end a connection (disconnect, revoke) even when its provider
    /// has been switched off for new connects and syncing. Without this, turning a provider off
    /// would strand the stored credentials of connections that already exist.
    /// </summary>
    bool TryGetForLifecycle(BankProvider provider, out IBankAggregator? aggregator);

    /// <summary>Providers that currently have a registered implementation.</summary>
    IReadOnlyCollection<BankProvider> AvailableProviders { get; }
}
