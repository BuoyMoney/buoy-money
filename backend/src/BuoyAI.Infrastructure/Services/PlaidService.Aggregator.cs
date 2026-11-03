using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Plaid's conformance to the provider-neutral <see cref="IBankAggregator"/> seam (#310). Thin
/// adapters over the existing Plaid-specific methods so Plaid is reachable through the registry /
/// generic connect endpoint / background sync exactly like any future provider (MX, Teller, …).
/// Behaviour is unchanged — these just re-expose existing flows under the neutral contract.
/// </summary>
public partial class PlaidService : IBankAggregator
{
    public BankProvider Provider => BankProvider.Plaid;

    public async Task<AggregatorConnectStart> StartConnectionAsync(int userId, string? platform)
    {
        var response = await CreateLinkTokenAsync(new CreateLinkTokenRequest { UserId = userId, Platform = platform });
        return new AggregatorConnectStart
        {
            Provider = BankProvider.Plaid,
            Kind = AggregatorConnectKind.LinkToken,
            Value = response.LinkToken,
        };
    }

    public async Task<AggregatorConnectResult> CompleteConnectionAsync(int userId, string artifact)
    {
        var response = await ExchangePublicTokenWithQuickSyncAsync(
            new ExchangePublicTokenRequest { UserId = userId, PublicToken = artifact });
        return new AggregatorConnectResult
        {
            Provider = BankProvider.Plaid,
            Success = true,
            ExternalConnectionId = response.ItemId,
        };
    }

    public Task<int> SyncConnectionTransactionsAsync(PlaidConnection connection, DateTime fromUtc, DateTime toUtc)
    {
        // Plaid auth is the encrypted access token on the connection.
        var accessToken = _encryptionService.Decrypt(connection.AccessToken);
        return SyncRecentTransactionsAsync(accessToken, fromUtc, toUtc);
    }

    // Plaid syncs every cycle; its webhooks and per-call rate limits carry the load.
    public TimeSpan MinimumSyncInterval => TimeSpan.Zero;
    public TimeSpan SyncJitter => TimeSpan.Zero;

    public Task DisconnectConnectionAsync(int userId, int connectionId) => DisconnectAccountAsync(userId, connectionId);

    public Task<bool> TryRemoveConnectionAsync(PlaidConnection connection, CancellationToken cancellationToken = default)
        => TryRemovePlaidItemAsync(connection, cancellationToken);
    // HandleSyncFailureAsync is already public on PlaidService and satisfies the interface.
}
