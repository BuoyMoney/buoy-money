using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// What the Accounts page needs from the server, whichever bank provider the connections came from
/// (Plaid, SimpleFIN or the Cloud bank feed). Reads and edits only this server's own tables, so it
/// runs in both editions; the Plaid-specific routes use it too, which keeps their answers identical.
/// </summary>
public class BankAccountService : IBankAccountService
{
  private readonly IUserRepository _users;
  private readonly IAccountRepository _accounts;
  private readonly ITransactionRepository _transactions;
  private readonly IPlaidConnectionRepository _connections;
  private readonly ILogger<BankAccountService> _logger;

  public BankAccountService(
    IUserRepository users,
    IAccountRepository accounts,
    ITransactionRepository transactions,
    IPlaidConnectionRepository connections,
    ILogger<BankAccountService> logger)
  {
    _users = users;
    _accounts = accounts;
    _transactions = transactions;
    _connections = connections;
    _logger = logger;
  }

  public async Task<IEnumerable<AccountDto>> GetAccountsAsync(int userId)
  {
    try
    {
      // Family-aware (parity with the transactions view): show the whole family's accounts, not
      // just the user's own. GetByUserOrFamilyAsync handles a null FamilyId (solo user) — it then
      // resolves to just the user's accounts.
      var user = await _users.GetByIdAsync(userId);
      var accounts = await _accounts.GetByUserOrFamilyAsync(userId, user?.FamilyId);

      return accounts.Select(a => new AccountDto
      {
        Id = a.Id,
        PlaidAccountId = a.PlaidAccountId,
        Name = a.Name,
        OfficialName = a.OfficialName,
        Nickname = a.Nickname,
        Mask = a.Mask,
        Type = a.Type,
        Subtype = a.Subtype,
        CurrentBalance = a.CurrentBalance,
        AvailableBalance = a.AvailableBalance,
        Currency = a.Currency,
        IsActive = a.IsActive,
        AssociatedUserId = a.AssociatedUserId
      });
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting accounts for user {UserId}", userId);
      throw;
    }
  }

  public async Task<IEnumerable<AccountConnectionDto>> GetAccountConnectionsAsync(int userId)
  {
    try
    {
      var user = await _users.GetByIdAsync(userId);
      
      _logger.LogInformation("GetAccountConnectionsAsync: UserId={UserId}, User.FamilyId={FamilyId}", 
        userId, user?.FamilyId);
      
      var connections = await _connections.GetByUserOrFamilyAsync(userId, user?.FamilyId);
      var result = new List<AccountConnectionDto>();

      _logger.LogInformation("GetAccountConnectionsAsync: Found {ConnectionCount} connections for user {UserId} (FamilyId={FamilyId})",
        connections.Count(), userId, user?.FamilyId);

      // Load the family's transaction history ONCE and build a per-account count
      // map. Previously this query ran inside the connection loop below — for a
      // user with several connections and a large history that was N full-table
      // scans, which pushed the endpoint past the mobile client's 30s timeout
      // ("accounts page won't load on Android": web tolerates the slow response,
      // native Dio enforces receiveTimeout and fails).
      var allTransactions = await _transactions.GetByUserOrFamilyAsync(userId, user?.FamilyId, null, null);
      var transactionCountByAccountId = allTransactions
        .GroupBy(t => t.AccountId)
        .ToDictionary(g => g.Key, g => g.Count());

      foreach (var connection in connections)
      {
        var accounts = await _accounts.GetByConnectionIdAsync(connection.Id);
        var connectionAccounts = accounts.Select(a => new AccountDto
          {
            Id = a.Id,
            PlaidAccountId = a.PlaidAccountId,
            Name = a.Name,
            OfficialName = a.OfficialName,
            Nickname = a.Nickname,
            Mask = a.Mask,
            Type = a.Type,
            Subtype = a.Subtype,
            CurrentBalance = a.CurrentBalance,
            AvailableBalance = a.AvailableBalance,
            Currency = a.Currency,
            IsActive = a.IsActive,
            OwnerUserId = a.UserId,
            OwnerName = a.User != null ? $"{a.User.FirstName} {a.User.LastName}".Trim() : connection.User?.FirstName,
            AssociatedUserId = a.AssociatedUserId
          }).ToList();

        var totalTransactions = accounts.Sum(a =>
          transactionCountByAccountId.TryGetValue(a.Id, out var count) ? count : 0);

        result.Add(new AccountConnectionDto
        {
          Id = connection.Id,
          InstitutionName = connection.InstitutionName,
          InstitutionId = connection.InstitutionId,
          Provider = connection.Provider.ToString(),
          LastSyncAt = connection.LastSyncAt,
          IsActive = connection.IsActive,
          RequiresUserAction = connection.RequiresUserAction,
          Accounts = connectionAccounts,
          TotalTransactions = totalTransactions,
          OwnerUserId = connection.UserId,
          OwnerName = connection.User != null ? $"{connection.User.FirstName} {connection.User.LastName}".Trim() : null
        });
      }

      return result;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting account connections for user {UserId}", userId);
      throw;
    }
  }

  public async Task<ImportProgressDto> GetImportProgressAsync(int userId, int connectionId)
  {
    try
    {
      var connection = await _connections.GetByIdAsync(connectionId);
      if (connection == null || connection.UserId != userId)
      {
        throw new ArgumentException("Connection not found or access denied");
      }

      return new ImportProgressDto
      {
        ConnectionId = connection.Id,
        Status = connection.ImportStatus.ToString(),
        Progress = connection.ImportProgress,
        Message = connection.ImportMessage,
        StartedAt = connection.ImportStartedAt,
        CompletedAt = connection.ImportCompletedAt,
        QuickSyncCompletedAt = connection.QuickSyncCompletedAt,
        CanDismiss = connection.ImportStatus == ImportStatus.Completed || 
                     connection.ImportStatus == ImportStatus.Failed
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting import progress {ConnectionId} for user {UserId}", connectionId, userId);
      throw;
    }
  }

  public async Task<UpdateAccountNicknameResponse> UpdateAccountNicknameAsync(int userId, int accountId, string? nickname)
  {
    try
    {
      var account = await _accounts.GetByIdAsync(accountId);
      
      if (account == null)
      {
        throw new KeyNotFoundException($"Account {accountId} not found");
      }
      
      if (account.UserId != userId)
      {
        throw new UnauthorizedAccessException($"User {userId} is not authorized to update account {accountId}");
      }
      
      var normalizedNickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
      
      account.Nickname = normalizedNickname;
      await _accounts.UpdateAsync(account);
      
      _logger.LogInformation("Updated nickname for account {AccountId} to '{Nickname}' by user {UserId}", 
        accountId, normalizedNickname ?? "(cleared)", userId);
      
      var displayName = normalizedNickname ?? account.OfficialName ?? account.Name;
      
      return new UpdateAccountNicknameResponse
      {
        AccountId = accountId,
        Nickname = normalizedNickname,
        DisplayName = displayName
      };
    }
    catch (KeyNotFoundException)
    {
      throw;
    }
    catch (UnauthorizedAccessException)
    {
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error updating nickname for account {AccountId}", accountId);
      throw;
    }
  }

  public async Task<AccountAssociationResponse> SetAccountAssociationAsync(int userId, int accountId, int? associatedUserId)
  {
    var account = await _accounts.GetByIdAsync(accountId)
      ?? throw new KeyNotFoundException($"Account {accountId} not found");

    var caller = await _users.GetByIdAsync(userId);
    var callerInAccountFamily = account.FamilyId != null && caller?.FamilyId == account.FamilyId;
    if (account.UserId != userId && !callerInAccountFamily)
      throw new UnauthorizedAccessException($"User {userId} is not authorized to update account {accountId}");

    // The associated user (if any) must belong to the same family as the account/caller.
    if (associatedUserId.HasValue)
    {
      var targetFamily = (await _users.GetByIdAsync(associatedUserId.Value))?.FamilyId;
      var accountFamily = account.FamilyId ?? caller?.FamilyId;
      if (targetFamily == null || targetFamily != accountFamily)
        throw new InvalidOperationException("The associated user must be a member of this family.");
    }

    var previous = account.AssociatedUserId;
    account.AssociatedUserId = associatedUserId;
    await _accounts.UpdateAsync(account);

    // Opt-in (per the caller's setting): also move this account's existing spend to the new owner.
    var reattributed = 0;
    if (previous != associatedUserId && caller?.ReattributeOnAccountAssociationChange == true)
      reattributed = await _transactions.ReattributeAccountClaimsAsync(accountId, previous, associatedUserId);

    _logger.LogInformation(
      "Set account {AccountId} association to {AssociatedUserId} by user {UserId} (reattributed {Count} existing)",
      accountId, associatedUserId?.ToString() ?? "(family)", userId, reattributed);

    return new AccountAssociationResponse { AccountId = accountId, AssociatedUserId = associatedUserId };
  }
}
