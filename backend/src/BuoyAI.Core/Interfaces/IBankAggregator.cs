using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Provider-neutral contract for a bank-aggregation provider (#310). Implement this + register it in
/// DI (<c>AddScoped&lt;IBankAggregator, MyAggregator&gt;()</c>) and the aggregator becomes reachable
/// through <see cref="IBankAggregatorRegistry"/> — the generic connect endpoint and the background
/// transaction sync dispatch to it automatically by <see cref="Provider"/>. See BuoyAI issue #748
/// for the full "add a provider" checklist + MX-specific setup.
///
/// <para>Account-type scope is enforced centrally by <see cref="AccountTypePolicy"/> — every
/// aggregator maps its raw types to our <see cref="AccountType"/> and drops non-budget accounts at
/// ingestion, so investment/brokerage/loans never surface regardless of provider.</para>
///
/// <para>Connections are stored in the <see cref="PlaidConnection"/> table (name predates the
/// abstraction) tagged with <see cref="PlaidConnection.Provider"/>; a provider stores its own
/// connection id in <c>ItemId</c> and its own auth secret (encrypted) in <c>AccessToken</c>.</para>
/// </summary>
public interface IBankAggregator
{
    /// <summary>Which provider this implementation serves. Must be unique across registered aggregators.</summary>
    BankProvider Provider { get; }

    /// <summary>
    /// Begin a connection for <paramref name="userId"/>: returns what the client launches (link
    /// token / widget URL / redirect). <paramref name="platform"/> is a client hint ("web"/"android"/"ios").
    /// </summary>
    Task<AggregatorConnectStart> StartConnectionAsync(int userId, string? platform);

    /// <summary>
    /// Complete a connection from the provider's post-connect artifact (<paramref name="artifact"/> =
    /// Plaid public_token / MX member_guid). Persists the connection (tagged with <see cref="Provider"/>)
    /// and kicks off the initial account+transaction sync.
    /// </summary>
    Task<AggregatorConnectResult> CompleteConnectionAsync(int userId, string artifact);

    /// <summary>
    /// Begin signing in again for one of the user's own connections that needs it, keeping the same connection.
    /// Only providers whose sign-in happens on a page of their own offer this; the rest refuse.
    /// </summary>
    Task<AggregatorConnectStart> StartReconnectAsync(int userId, int connectionId, string? platform) =>
        throw new NotSupportedException($"{Provider} does not reconnect this way.");

    /// <summary>
    /// Sync one connection's recent transactions (and refresh its account balances) over the given
    /// UTC window; returns the number of new transactions. The aggregator resolves its own auth from
    /// <paramref name="connection"/>. Called per-connection by the background sync loop.
    /// </summary>
    Task<int> SyncConnectionTransactionsAsync(PlaidConnection connection, DateTime fromUtc, DateTime toUtc);

    /// <summary>
    /// The least time the background loop leaves between sync attempts on one connection. The loop
    /// wakes every ten minutes for every provider; a provider with a request budget (SimpleFIN
    /// expects 24 a day) sets a longer wait here. Zero means "every cycle", which is Plaid today.
    /// </summary>
    TimeSpan MinimumSyncInterval { get; }

    /// <summary>
    /// Extra wait, up to this long, added per connection on top of <see cref="MinimumSyncInterval"/>
    /// so connections do not all call the provider in the same minute. Zero for no spread.
    /// </summary>
    TimeSpan SyncJitter { get; }

    /// <summary>
    /// The user disconnects one of their own connections: revokes it at the provider where the
    /// provider has a way to, then marks it inactive. Throws <see cref="ArgumentException"/> when
    /// the connection does not exist or belongs to someone else.
    /// </summary>
    Task DisconnectConnectionAsync(int userId, int connectionId);

    /// <summary>
    /// Revokes the connection at the provider and nothing else (the caller owns the row). True when
    /// the provider side is done or there is nothing to revoke; false on a transient failure the
    /// caller should retry. Never throws.
    /// </summary>
    Task<bool> TryRemoveConnectionAsync(PlaidConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed sync. A failure that needs the user (the login is no longer valid, or too
    /// many failures in a row) moves the connection out of Active and alerts the user; anything
    /// else is noted and retried next cycle. Never throws.
    /// </summary>
    Task HandleSyncFailureAsync(int connectionId, string errorMessage);

    /// <summary>
    /// Brings the status of connections that are not syncing (needs sign-in) in line with what
    /// the provider now says, so one that healed at the bank clears with no action by the user. Only the Cloud bank feed
    /// has anything to ask; the rest do nothing. Never throws.
    /// </summary>
    Task ReconcileConnectionsAsync(IReadOnlyCollection<PlaidConnection> connections) => Task.CompletedTask;
}
