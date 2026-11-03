using BuoyAI.Core.DTOs.Plaid;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Provider-neutral account and connection reads and edits for the Accounts page. Available in every
/// edition: it touches only this server's own data, never a bank provider's API.
/// </summary>
public interface IBankAccountService
{
  /// <summary>The user's accounts and their family's, every provider.</summary>
  Task<IEnumerable<AccountDto>> GetAccountsAsync(int userId);

  /// <summary>The user's bank connections and their family's, with accounts and transaction counts.</summary>
  Task<IEnumerable<AccountConnectionDto>> GetAccountConnectionsAsync(int userId);

  Task<ImportProgressDto> GetImportProgressAsync(int userId, int connectionId);

  Task<UpdateAccountNicknameResponse> UpdateAccountNicknameAsync(int userId, int accountId, string? nickname);

  Task<AccountAssociationResponse> SetAccountAssociationAsync(int userId, int accountId, int? associatedUserId);
}
