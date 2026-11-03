using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The one place a "your bank connection needs attention" alert is raised, for every provider.
/// The in-app notification (with its push copy) goes through <see cref="INotificationService"/>
/// as it always has; the email is new and is sent at most once per outage (the flag clears when
/// the connection syncs again, so the next outage emails again).
/// </summary>
public class BankConnectionAlertService : IBankConnectionAlertService
{
    private readonly INotificationService _notifications;
    private readonly IEmailService _email;
    private readonly IUserRepository _users;
    private readonly IPlaidConnectionRepository _connections;
    private readonly ILogger<BankConnectionAlertService> _logger;

    public BankConnectionAlertService(
        INotificationService notifications,
        IEmailService email,
        IUserRepository users,
        IPlaidConnectionRepository connections,
        ILogger<BankConnectionAlertService> logger)
    {
        _notifications = notifications;
        _email = email;
        _users = users;
        _connections = connections;
        _logger = logger;
    }

    public async Task NotifyNeedsAttentionAsync(PlaidConnection connection, string? reasonCode = null)
    {
        var reason = reasonCode ?? ReasonFor(connection.Status);
        if (connection.Provider == BankProvider.Plaid)
        {
            await _notifications.CreatePlaidUpdateNotificationAsync(
                connection.UserId, connection.Id, connection.InstitutionName, reason);
        }
        else if (connection.Provider == BankProvider.Bridge)
        {
            // A Cloud bank feed is fixed from the accounts page, not the SimpleFIN settings page.
            await _notifications.CreateBridgeAttentionNotificationAsync(
                connection.UserId, connection.Id, connection.InstitutionName, reason);
        }
        else
        {
            // Plaid's update page does not apply here; send the user to the provider's own page.
            await _notifications.CreateSimpleFinAttentionNotificationAsync(
                connection.UserId, connection.Id, connection.InstitutionName, reason);
        }

        await TrySendEmailOnceAsync(connection);
    }

    public async Task NotifyResolvedAsync(PlaidConnection connection)
    {
        try
        {
            await _notifications.UpdateConnectionNotificationToResolvedAsync(
                connection.UserId, connection.Id, connection.InstitutionName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve the bank connection notification for connection {ConnectionId}", connection.Id);
        }
    }

    /// <summary>The notification wording for a status, using the codes the notification text already knows.</summary>
    internal static string ReasonFor(PlaidItemStatus status) => status switch
    {
        PlaidItemStatus.LoginRequired => "ITEM_LOGIN_REQUIRED",
        PlaidItemStatus.PendingExpiration => "PENDING_EXPIRATION",
        PlaidItemStatus.PendingDisconnect => "PENDING_DISCONNECT",
        _ => "SYNC_FAILED"
    };

    private async Task TrySendEmailOnceAsync(PlaidConnection connection)
    {
        try
        {
            // The email template says the bank is disconnected, so it goes only to connections that
            // really are broken. Pending-expiration and pending-disconnect still work today, and
            // skipping them here leaves the email free for the real outage that follows.
            if (connection.Status is not (PlaidItemStatus.LoginRequired or PlaidItemStatus.Error))
            {
                return;
            }

            if (connection.AlertSentAt != null)
            {
                return;
            }

            var user = await _users.GetByIdAsync(connection.UserId);
            if (user == null || string.IsNullOrWhiteSpace(user.Email))
            {
                return;
            }

            // Concurrent webhooks and syncs can alert the same connection at once, so the right to
            // send is taken with one conditional update of this column alone. Whoever gets false
            // lets the other send; nothing here rewrites the whole (possibly stale) row.
            var now = DateTime.UtcNow;
            if (!await _connections.TryClaimAlertEmailAsync(connection.Id, now))
            {
                return;
            }
            connection.AlertSentAt = now;

            try
            {
                // The email service honours the user's "connection issues" email preference and
                // swallows its own send errors.
                await _email.SendPlaidConnectionIssueAsync(user.Email, user.FirstName, connection.InstitutionName);
            }
            catch
            {
                connection.AlertSentAt = null;
                await _connections.ReleaseAlertEmailClaimAsync(connection.Id);
                throw;
            }
        }
        catch (Exception ex)
        {
            // An email problem must not undo the status change or the in-app alert.
            _logger.LogWarning(ex, "Could not send the bank connection alert email for connection {ConnectionId}", connection.Id);
        }
    }
}
