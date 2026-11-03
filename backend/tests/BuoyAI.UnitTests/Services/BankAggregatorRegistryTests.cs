using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The provider registry (#310) — resolving/dispatching <see cref="IBankAggregator"/> by provider,
/// which is what makes adding an aggregator a single DI registration.
/// </summary>
public class BankAggregatorRegistryTests
{
    private sealed class FakeAggregator : IBankAggregator
    {
        public FakeAggregator(BankProvider provider) => Provider = provider;
        public BankProvider Provider { get; }
        public Task<AggregatorConnectStart> StartConnectionAsync(int userId, string? platform) =>
            Task.FromResult(new AggregatorConnectStart { Provider = Provider });
        public Task<AggregatorConnectResult> CompleteConnectionAsync(int userId, string artifact) =>
            Task.FromResult(new AggregatorConnectResult { Provider = Provider, Success = true });
        public Task<int> SyncConnectionTransactionsAsync(PlaidConnection connection, DateTime fromUtc, DateTime toUtc) =>
            Task.FromResult(0);
        public TimeSpan MinimumSyncInterval => TimeSpan.Zero;
        public TimeSpan SyncJitter => TimeSpan.Zero;
        public Task DisconnectConnectionAsync(int userId, int connectionId) => Task.CompletedTask;
        public Task<bool> TryRemoveConnectionAsync(PlaidConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task HandleSyncFailureAsync(int connectionId, string errorMessage) => Task.CompletedTask;
    }

    [Fact]
    public void ALifecycleOnlyProvider_IsNotOffered_ButCanStillBeReachedToEndAConnection()
    {
        var plaid = new FakeAggregator(BankProvider.Plaid);
        var held = new FakeAggregator(BankProvider.SimpleFin);
        var registry = new BankAggregatorRegistry(new IBankAggregator[] { plaid }, new[] { new LifecycleOnlyAggregator(held) });

        registry.AvailableProviders.Should().BeEquivalentTo(new[] { BankProvider.Plaid });
        registry.TryGet(BankProvider.SimpleFin, out _).Should().BeFalse();
        registry.TryGetForLifecycle(BankProvider.SimpleFin, out var found).Should().BeTrue();
        found.Should().BeSameAs(held);
        registry.TryGetForLifecycle(BankProvider.Plaid, out var live).Should().BeTrue();
        live.Should().BeSameAs(plaid);
    }

    [Fact]
    public void Get_ReturnsTheAggregatorRegisteredForThatProvider()
    {
        var plaid = new FakeAggregator(BankProvider.Plaid);
        var mx = new FakeAggregator(BankProvider.Mx);
        var registry = new BankAggregatorRegistry(new IBankAggregator[] { plaid, mx });

        registry.Get(BankProvider.Plaid).Should().BeSameAs(plaid);
        registry.Get(BankProvider.Mx).Should().BeSameAs(mx);
    }

    [Fact]
    public void Get_Throws_WhenNoAggregatorIsRegisteredForTheProvider()
    {
        var registry = new BankAggregatorRegistry(new IBankAggregator[] { new FakeAggregator(BankProvider.Plaid) });

        var act = () => registry.Get(BankProvider.Mx);

        act.Should().Throw<NotSupportedException>().WithMessage("*Mx*");
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForUnregisteredProvider()
    {
        var registry = new BankAggregatorRegistry(new IBankAggregator[] { new FakeAggregator(BankProvider.Plaid) });

        registry.TryGet(BankProvider.Teller, out var aggregator).Should().BeFalse();
        aggregator.Should().BeNull();
    }

    [Fact]
    public void Constructor_Throws_OnDuplicateProviderRegistration()
    {
        var act = () => new BankAggregatorRegistry(new IBankAggregator[]
        {
            new FakeAggregator(BankProvider.Plaid),
            new FakeAggregator(BankProvider.Plaid),
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Plaid*");
    }

    [Fact]
    public void AvailableProviders_ListsEveryRegisteredProvider()
    {
        var registry = new BankAggregatorRegistry(new IBankAggregator[]
        {
            new FakeAggregator(BankProvider.Plaid),
            new FakeAggregator(BankProvider.Mx),
        });

        registry.AvailableProviders.Should().BeEquivalentTo(new[] { BankProvider.Plaid, BankProvider.Mx });
    }
}
