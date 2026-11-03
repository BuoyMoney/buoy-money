using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Infrastructure.Services;

public class PlaidWebhookService : IPlaidWebhookService
{
    private readonly IPlaidService _plaidService;
    private readonly IPlaidConnectionRepository _plaidConnectionRepository;
    private readonly INotificationService _notificationService;
    private readonly IBankConnectionAlertService _alerts;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ILogger<PlaidWebhookService> _logger;

    public PlaidWebhookService(
        IPlaidService plaidService,
        IPlaidConnectionRepository plaidConnectionRepository,
        INotificationService notificationService,
        IBankConnectionAlertService alerts,
        ISubscriptionService subscriptionService,
        ILogger<PlaidWebhookService> logger)
    {
        _plaidService = plaidService;
        _plaidConnectionRepository = plaidConnectionRepository;
        _notificationService = notificationService;
        _alerts = alerts;
        _subscriptionService = subscriptionService;
        _logger = logger;
    }

    public async Task ProcessWebhookAsync(PlaidWebhookRequest webhook)
    {
        try
        {
            _logger.LogInformation("Processing webhook: {WebhookType} for item {ItemId}", 
                webhook.WebhookType, webhook.ItemId);

            var connection = await _plaidService.GetConnectionByItemIdAsync(webhook.ItemId);
            if (connection == null)
            {
                _logger.LogWarning("Received webhook for unknown item {ItemId}", webhook.ItemId);
                return;
            }

            switch (webhook.WebhookType?.ToUpperInvariant())
            {
                case "ITEM":
                    await HandleItemWebhook(connection, webhook);
                    break;
                case "TRANSACTIONS":
                    await HandleTransactionsWebhook(connection, webhook);
                    break;
                case "ACCOUNTS":
                    await HandleAccountsWebhook(connection, webhook);
                    break;
                default:
                    _logger.LogInformation("Unhandled webhook type: {WebhookType}", webhook.WebhookType);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing webhook: {WebhookType} for item {ItemId}", 
                webhook.WebhookType, webhook.ItemId);
            throw;
        }
    }

    private async Task HandleItemWebhook(PlaidConnection connection, PlaidWebhookRequest webhook)
    {
        switch (webhook.WebhookCode?.ToUpperInvariant())
        {
            case "ERROR":
                if (webhook.Error != null)
                {
                    await HandleItemErrorAsync(connection.Id, webhook.Error);
                }
                break;
            case "PENDING_EXPIRATION":
                var expirationDate = DateTime.UtcNow.AddDays(7); // Default to 7 days
                await HandlePendingExpirationAsync(connection.Id, expirationDate);
                break;
            case "PENDING_DISCONNECT":
                // Plaid gives ~7 days' notice before disconnecting the item.
                await HandlePendingDisconnectAsync(connection.ItemId, DateTime.UtcNow.AddDays(7));
                break;
            case "LOGIN_REPAIRED":
                // The item healed outside our app (bank-side fix) — clear the prompts.
                await HandleLoginRepairedAsync(connection.ItemId);
                break;
            case "NEW_ACCOUNTS_AVAILABLE":
                // Payload carries no account ids; it's a signal to offer account selection.
                await HandleNewAccountsAvailableAsync(connection.ItemId, Array.Empty<string>());
                break;
            case "USER_PERMISSION_REVOKED":
                await HandleUserPermissionRevokedAsync(connection.Id);
                break;
            case "WEBHOOK_UPDATE_ACKNOWLEDGED":
                _logger.LogInformation("Webhook update acknowledged for item {ItemId}", webhook.ItemId);
                break;
            default:
                _logger.LogInformation("Unhandled item webhook code: {WebhookCode}", webhook.WebhookCode);
                break;
        }
    }

    private async Task HandleTransactionsWebhook(PlaidConnection connection, PlaidWebhookRequest webhook)
    {
        switch (webhook.WebhookCode?.ToUpperInvariant())
        {
            case "SYNC_UPDATES_AVAILABLE":
            case "DEFAULT_UPDATE":
                // Trigger transaction sync for this user
                try
                {
                    await _plaidService.SyncTransactionsAsync(connection.UserId);
                    _logger.LogInformation("Triggered transaction sync for user {UserId} after webhook", 
                        connection.UserId);
                    
                    // After successful transaction sync, trigger on-demand subscription detection
                    // This is subject to a 7-day cooldown per user
                    try
                    {
                        var detectionRan = await _subscriptionService.TriggerOnDemandDetectionAsync(connection.UserId);
                        if (detectionRan)
                        {
                            _logger.LogInformation(
                                "Triggered on-demand subscription detection for user {UserId} after Plaid sync", 
                                connection.UserId);
                        }
                    }
                    catch (Exception subEx)
                    {
                        _logger.LogWarning(subEx, 
                            "Failed to run on-demand subscription detection for user {UserId}", 
                            connection.UserId);
                        // Don't fail the webhook - subscription detection is a nice-to-have
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing transactions for user {UserId} after webhook",
                        connection.UserId);
                }
                break;
            case "TRANSACTIONS_REMOVED":
                // Plaid dropped one or more transactions at the source (cancelled pending charge
                // or reversed posted transaction). The plain sync paths only create/update by
                // PlaidTransactionId and never delete, so without this the stale rows persist and
                // permanently skew spend totals, budgets, and AI insights.
                if (webhook.RemovedTransactions is { Count: > 0 })
                {
                    try
                    {
                        var removed = await _plaidService.RemoveTransactionsAsync(webhook.RemovedTransactions);
                        _logger.LogInformation(
                            "TRANSACTIONS_REMOVED for item {ItemId}: purged {Removed} of {Reported} reported transactions",
                            webhook.ItemId, removed, webhook.RemovedTransactions.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error removing transactions for item {ItemId} after webhook", webhook.ItemId);
                    }
                }
                else
                {
                    _logger.LogInformation("TRANSACTIONS_REMOVED for item {ItemId} carried no transaction ids", webhook.ItemId);
                }
                break;
            default:
                _logger.LogInformation("Unhandled transactions webhook code: {WebhookCode}", webhook.WebhookCode);
                break;
        }
    }

    private async Task HandleAccountsWebhook(PlaidConnection connection, PlaidWebhookRequest webhook)
    {
        switch (webhook.WebhookCode?.ToUpperInvariant())
        {
            case "DEFAULT_UPDATE":
                // Trigger account sync for this user
                try
                {
                    await _plaidService.GetAccountsAsync(connection.UserId);
                    _logger.LogInformation("Triggered account sync for user {UserId} after webhook", 
                        connection.UserId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing accounts for user {UserId} after webhook", 
                        connection.UserId);
                }
                break;
            default:
                _logger.LogInformation("Unhandled accounts webhook code: {WebhookCode}", webhook.WebhookCode);
                break;
        }
    }

    public async Task<CreateUpdateTokenResponse> CreateUpdateTokenAsync(int userId, int connectionId, bool accountSelectionEnabled = false, string? platform = null)
    {
        try
        {
            var linkToken = await _plaidService.CreateUpdateTokenAsync(userId, connectionId, accountSelectionEnabled, platform);
            return new CreateUpdateTokenResponse
            {
                LinkToken = linkToken,
                Success = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating update token for user {UserId}, connection {ConnectionId}", 
                userId, connectionId);
            throw;
        }
    }

    public async Task<bool> UpdateConnectionAsync(int userId, int connectionId, UpdateConnectionRequest request)
    {
        try
        {
            // Ownership check mirrors PlaidService.CreateUpdateTokenAsync: only the owner
            // of the connection may update it, otherwise this is an IDOR.
            var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
            if (connection == null || connection.UserId != userId)
            {
                throw new UnauthorizedAccessException("Connection not found or access denied");
            }

            return await _plaidService.HandleItemUpdateAsync(connectionId, request.PublicToken);
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating connection {ConnectionId}", connectionId);
            return false;
        }
    }

    public async Task<ConnectionStatusResponse> GetConnectionStatusAsync(int userId, int connectionId)
    {
        try
        {
            var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
            if (connection == null)
            {
                throw new ArgumentException("Connection not found");
            }

            // Ownership check: a caller may only read the status of their own connection.
            if (connection.UserId != userId)
            {
                throw new UnauthorizedAccessException("Connection not found or access denied");
            }

            return new ConnectionStatusResponse
            {
                ConnectionId = connectionId,
                Status = connection.Status,
                RequiresUserAction = connection.RequiresUserAction,
                LastError = connection.LastError,
                LastStatusUpdate = connection.LastStatusUpdate,
                ExpirationDate = connection.ExpirationDate,
                InstitutionName = connection.InstitutionName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting connection status for {ConnectionId}", connectionId);
            throw;
        }
    }

    public async Task HandleItemErrorAsync(int connectionId, PlaidError error)
    {
        try
        {
            var status = error.ErrorCode?.ToUpperInvariant() switch
            {
                "ITEM_LOGIN_REQUIRED" => PlaidItemStatus.LoginRequired,
                "ACCESS_NOT_GRANTED" => PlaidItemStatus.LoginRequired,
                "INSUFFICIENT_CREDENTIALS" => PlaidItemStatus.LoginRequired,
                "INVALID_CREDENTIALS" => PlaidItemStatus.LoginRequired,
                "INVALID_MFA" => PlaidItemStatus.LoginRequired,
                "ITEM_LOCKED" => PlaidItemStatus.LoginRequired,
                "USER_SETUP_REQUIRED" => PlaidItemStatus.LoginRequired,
                "MFA_NOT_SUPPORTED" => PlaidItemStatus.LoginRequired,
                "INVALID_SEND_METHOD" => PlaidItemStatus.LoginRequired,
                "NO_AUTH_ACCOUNTS" => PlaidItemStatus.LoginRequired,
                "PRODUCT_NOT_AVAILABLE" => PlaidItemStatus.Error,
                "ITEM_NOT_SUPPORTED" => PlaidItemStatus.Error,
                "TOO_MANY_VERIFICATION_ATTEMPTS" => PlaidItemStatus.Error,
                "INSTITUTION_DOWN" => PlaidItemStatus.Error,
                "INSTITUTION_NOT_RESPONDING" => PlaidItemStatus.Error,
                _ => PlaidItemStatus.Error
            };

            var errorMessage = $"{error.ErrorCode}: {error.ErrorMessage}";
            await _plaidService.UpdateConnectionStatusAsync(connectionId, status, errorMessage);

            // Get connection details for notification
            var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
            if (connection != null && status == PlaidItemStatus.LoginRequired)
            {
                // Create notification for user
                await _alerts.NotifyNeedsAttentionAsync(connection, error.ErrorCode ?? "UNKNOWN_ERROR");
            }

            _logger.LogWarning("Updated connection {ConnectionId} status to {Status} due to error: {ErrorMessage}", 
                connectionId, status, errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling item error for connection {ConnectionId}", connectionId);
            throw;
        }
    }

    public async Task HandleUserPermissionRevokedAsync(int connectionId)
    {
        try
        {
            await _plaidService.HandleUserPermissionRevokedAsync(connectionId);
            _logger.LogInformation("Handled user permission revoked for connection {ConnectionId}", connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling user permission revoked for connection {ConnectionId}", connectionId);
            throw;
        }
    }

    public async Task HandlePendingExpirationAsync(int connectionId, DateTime expirationDate)
    {
        try
        {
            await _plaidService.HandlePendingExpirationAsync(connectionId, expirationDate);
            _logger.LogInformation("Handled pending expiration for connection {ConnectionId}, expires {ExpirationDate}", 
                connectionId, expirationDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling pending expiration for connection {ConnectionId}", connectionId);
            throw;
        }
    }

    public async Task HandleItemLoginRequiredAsync(string itemId, PlaidError error)
    {
        try
        {
            var connection = await _plaidService.GetConnectionByItemIdAsync(itemId);
            if (connection != null)
            {
                await _plaidService.UpdateConnectionStatusAsync(connection.Id, PlaidItemStatus.LoginRequired, error.ErrorMessage);
                _logger.LogInformation("Handled login required for item {ItemId}", itemId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling login required for item {ItemId}", itemId);
            throw;
        }
    }

    public async Task HandlePendingExpirationAsync(string itemId, DateTime expirationDate)
    {
        try
        {
            var connection = await _plaidService.GetConnectionByItemIdAsync(itemId);
            if (connection != null)
            {
                await _plaidService.HandlePendingExpirationAsync(connection.Id, expirationDate);
                _logger.LogInformation("Handled pending expiration for item {ItemId}, expires {ExpirationDate}", 
                    itemId, expirationDate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling pending expiration for item {ItemId}", itemId);
            throw;
        }
    }

    public async Task HandlePendingDisconnectAsync(string itemId, DateTime disconnectDate)
    {
        try
        {
            var connection = await _plaidService.GetConnectionByItemIdAsync(itemId);
            if (connection != null)
            {
                await _plaidService.UpdateConnectionStatusAsync(connection.Id, PlaidItemStatus.PendingDisconnect);
                // Update-mode entry point (Plaid prod-readiness checklist): the user gets the
                // "will be disconnected" notification with the /update-connection action.
                await _alerts.NotifyNeedsAttentionAsync(connection, "PENDING_DISCONNECT");
                _logger.LogInformation("Handled pending disconnect for item {ItemId}, disconnect date {DisconnectDate}",
                    itemId, disconnectDate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling pending disconnect for item {ItemId}", itemId);
            throw;
        }
    }

    public async Task HandleLoginRepairedAsync(string itemId)
    {
        try
        {
            var connection = await _plaidService.GetConnectionByItemIdAsync(itemId);
            if (connection != null)
            {
                await _plaidService.UpdateConnectionStatusAsync(connection.Id, PlaidItemStatus.Active);
                // The item healed without our update-mode flow (bank-side repair). Flip the
                // outstanding "connection needs attention" notification to resolved so the
                // user isn't nagged into a pointless re-auth.
                await _notificationService.UpdateConnectionNotificationToResolvedAsync(
                    connection.UserId, connection.Id, connection.InstitutionName);
                _logger.LogInformation("Handled login repaired for item {ItemId}", itemId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling login repaired for item {ItemId}", itemId);
            throw;
        }
    }

    public async Task HandleNewAccountsAvailableAsync(string itemId, string[] newAccountIds)
    {
        try
        {
            var connection = await _plaidService.GetConnectionByItemIdAsync(itemId);
            if (connection != null)
            {
                // Prompt the user into update mode with account selection enabled so they can
                // add the new accounts (the webhook itself carries no account ids).
                await _notificationService.CreatePlaidNewAccountsNotificationAsync(
                    connection.UserId, connection.Id, connection.InstitutionName);
            }
            _logger.LogInformation("New accounts available for item {ItemId}: {AccountIds}",
                itemId, string.Join(", ", newAccountIds));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling new accounts available for item {ItemId}", itemId);
            throw;
        }
    }
}
