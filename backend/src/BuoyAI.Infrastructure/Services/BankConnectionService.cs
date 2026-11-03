using BuoyAI.Core.DTOs;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services;

public class BankConnectionService : IBankConnectionService
{
    private readonly IPlaidConnectionRepository _connections;
    private readonly IAccountRepository _accounts;
    private readonly IUserRepository _users;
    private readonly IBankAggregatorRegistry _registry;

    public BankConnectionService(
        IPlaidConnectionRepository connections,
        IAccountRepository accounts,
        IUserRepository users,
        IBankAggregatorRegistry registry)
    {
        _connections = connections;
        _accounts = accounts;
        _users = users;
        _registry = registry;
    }

    public async Task<IReadOnlyList<BankConnectionSummaryDto>> ListAsync(int userId)
    {
        var user = await _users.GetByIdAsync(userId);
        var connections = await _connections.GetByUserOrFamilyAsync(userId, user?.FamilyId);

        var counts = await _accounts.CountActiveByConnectionIdsAsync(connections.Select(c => c.Id).ToList());
        var result = new List<BankConnectionSummaryDto>();
        foreach (var connection in connections)
        {
            result.Add(new BankConnectionSummaryDto
            {
                Id = connection.Id,
                Provider = connection.Provider.ToString(),
                InstitutionName = connection.InstitutionName,
                IsActive = connection.IsActive,
                RequiresUserAction = connection.RequiresUserAction,
                Status = connection.Status.ToString(),
                LastSyncAt = connection.LastTransactionSyncAt,
                Message = connection.RequiresUserAction ? connection.LastError : null,
                AccountCount = counts.GetValueOrDefault(connection.Id),
            });
        }
        return result;
    }

    public async Task<bool> DisconnectAsync(int userId, int connectionId)
    {
        var connection = await _connections.GetByIdAsync(connectionId);
        if (connection == null || connection.UserId != userId || !_registry.TryGetForLifecycle(connection.Provider, out var aggregator))
        {
            return false;
        }

        await aggregator!.DisconnectConnectionAsync(userId, connectionId);

        // The accounts leave with the connection. Left active they stayed in the account list and in
        // net worth while the Accounts page (which lists by connection) showed none. The transactions
        // stay, as they do when a bank link is removed on the hosted service, and the same accounts
        // come back active if the bank is added again (the aggregators reactivate them on sync).
        foreach (var account in (await _accounts.GetByConnectionIdAsync(connectionId)).ToList())
        {
            account.IsActive = false;
            await _accounts.UpdateAsync(account);
        }
        return true;
    }
}
