using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Tells the user a bank connection needs attention, whichever provider it came from. Every place
/// that moves a connection into a "needs the user" state calls this once with the saved
/// connection, so Plaid webhooks, Plaid sync failures and SimpleFIN failures all alert the same way:
/// an in-app notification with a push copy, and an email.
/// </summary>
public interface IBankConnectionAlertService
{
    /// <summary>
    /// Sends the in-app notification (which also pushes) every time, and the email at most once per
    /// outage (<see cref="PlaidConnection.AlertSentAt"/> is claimed atomically before it goes out and
    /// cleared when the connection syncs again; there is no timed repeat). <paramref name="reasonCode"/> picks the wording; when null it
    /// comes from the connection's own status. The email never throws.
    /// </summary>
    Task NotifyNeedsAttentionAsync(PlaidConnection connection, string? reasonCode = null);

    /// <summary>
    /// The connection is healthy again: the open "needs attention" notification becomes "connection restored".
    /// Never throws.
    /// </summary>
    Task NotifyResolvedAsync(PlaidConnection connection);
}
