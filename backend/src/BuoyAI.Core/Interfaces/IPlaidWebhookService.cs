using BuoyAI.Core.DTOs.Plaid;

namespace BuoyAI.Core.Interfaces;

public interface IPlaidWebhookService
{
    Task HandleItemLoginRequiredAsync(string itemId, PlaidError error);
    Task HandlePendingExpirationAsync(string itemId, DateTime expirationDate);
    Task HandlePendingDisconnectAsync(string itemId, DateTime disconnectDate);
    Task HandleLoginRepairedAsync(string itemId);
    Task HandleNewAccountsAvailableAsync(string itemId, string[] newAccountIds);
    Task ProcessWebhookAsync(PlaidWebhookRequest request);
    
    // API Methods
    /// <param name="accountSelectionEnabled">Opens Link update mode with account selection so
    /// the user can add newly available accounts (NEW_ACCOUNTS_AVAILABLE flow).</param>
    Task<CreateUpdateTokenResponse> CreateUpdateTokenAsync(int userId, int connectionId, bool accountSelectionEnabled = false, string? platform = null);
    Task<bool> UpdateConnectionAsync(int userId, int connectionId, UpdateConnectionRequest request);
    Task<ConnectionStatusResponse> GetConnectionStatusAsync(int userId, int connectionId);
}
