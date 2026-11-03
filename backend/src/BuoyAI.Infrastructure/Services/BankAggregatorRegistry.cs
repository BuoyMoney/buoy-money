using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Indexes every registered <see cref="IBankAggregator"/> by its <see cref="IBankAggregator.Provider"/>.
/// A new provider becomes reachable simply by being registered in DI — no change here.
/// </summary>
public class BankAggregatorRegistry : IBankAggregatorRegistry
{
    private readonly IReadOnlyDictionary<BankProvider, IBankAggregator> _byProvider;
    private readonly IReadOnlyDictionary<BankProvider, IBankAggregator> _lifecycleOnly;

    public BankAggregatorRegistry(IEnumerable<IBankAggregator> aggregators, IEnumerable<LifecycleOnlyAggregator>? lifecycleOnly = null)
    {
        var map = new Dictionary<BankProvider, IBankAggregator>();
        foreach (var aggregator in aggregators)
        {
            // Duplicate provider registration is a wiring bug — fail fast rather than silently drop one.
            if (!map.TryAdd(aggregator.Provider, aggregator))
                throw new InvalidOperationException(
                    $"More than one IBankAggregator is registered for provider '{aggregator.Provider}'.");
        }
        _byProvider = map;
        _lifecycleOnly = (lifecycleOnly ?? [])
            .Where(l => !map.ContainsKey(l.Aggregator.Provider))
            .GroupBy(l => l.Aggregator.Provider)
            .ToDictionary(g => g.Key, g => g.First().Aggregator);
    }

    public IBankAggregator Get(BankProvider provider) =>
        _byProvider.TryGetValue(provider, out var aggregator)
            ? aggregator
            : throw new NotSupportedException(
                $"No bank aggregator is registered for provider '{provider}'. Register one with " +
                $"services.AddScoped<IBankAggregator, {provider}Aggregator>() (see issue #748).");

    public bool TryGet(BankProvider provider, out IBankAggregator? aggregator) =>
        _byProvider.TryGetValue(provider, out aggregator);

    public bool TryGetForLifecycle(BankProvider provider, out IBankAggregator? aggregator) =>
        _byProvider.TryGetValue(provider, out aggregator) || _lifecycleOnly.TryGetValue(provider, out aggregator);

    public IReadOnlyCollection<BankProvider> AvailableProviders => _byProvider.Keys.ToList();
}

/// <summary>
/// An aggregator held back from <see cref="IBankAggregatorRegistry.TryGet"/> (so it is not offered,
/// connected or synced) but still able to end connections that already exist.
/// </summary>
public sealed record LifecycleOnlyAggregator(IBankAggregator Aggregator);
