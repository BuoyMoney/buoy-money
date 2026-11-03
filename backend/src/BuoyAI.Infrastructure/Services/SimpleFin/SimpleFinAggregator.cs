using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services.SimpleFin;

/// <summary>
/// SimpleFIN as the second <see cref="IBankAggregator"/>. The user makes a setup token at the
/// SimpleFIN bridge and pastes it here; the access URL it turns into is a bearer credential, so it
/// is stored encrypted in <see cref="PlaidConnection.AccessToken"/> and never logged.
///
/// The bridge expects about 24 requests a day per token, so this provider asks the sync loop to
/// leave at least <see cref="MinimumSyncInterval"/> between attempts.
/// </summary>
public class SimpleFinAggregator : IBankAggregator
{
    // The same threshold Plaid uses: this many failures in a row and the connection needs the user.
    private const int MaxConsecutiveSyncFailures = 4;

    // SimpleFIN advises overlapping each window with the one before by 5 days so late-posting
    // transactions are not missed. The sync loop already adds one day; this adds the rest.
    private const int ExtraOverlapDays = 4;

    private const int MaxAccountNameLength = 100;

    private readonly SimpleFinClient _client;
    private readonly SimpleFinOptions _options;
    private readonly IPlaidConnectionRepository _connections;
    private readonly IAccountRepository _accounts;
    private readonly IUserRepository _users;
    private readonly IEncryptionService _encryption;
    private readonly ITransactionIngestService _ingest;
    private readonly IBankConnectionAlertService _alerts;
    private readonly ILogger<SimpleFinAggregator> _logger;

    public SimpleFinAggregator(
        SimpleFinClient client,
        IOptions<SimpleFinOptions> options,
        IPlaidConnectionRepository connections,
        IAccountRepository accounts,
        IUserRepository users,
        IEncryptionService encryption,
        ITransactionIngestService ingest,
        IBankConnectionAlertService alerts,
        ILogger<SimpleFinAggregator> logger)
    {
        _client = client;
        _options = options.Value;
        _connections = connections;
        _accounts = accounts;
        _users = users;
        _encryption = encryption;
        _ingest = ingest;
        _alerts = alerts;
        _logger = logger;
    }

    public BankProvider Provider => BankProvider.SimpleFin;

    public TimeSpan MinimumSyncInterval => TimeSpan.FromHours(Math.Max(0, _options.MinSyncIntervalHours));

    public TimeSpan SyncJitter => TimeSpan.FromMinutes(Math.Max(0, _options.JitterMinutes));

    public Task<AggregatorConnectStart> StartConnectionAsync(int userId, string? platform) =>
        Task.FromResult(new AggregatorConnectStart
        {
            Provider = BankProvider.SimpleFin,
            Kind = AggregatorConnectKind.SetupTokenPrompt,
            Value = _options.BridgeUrl,
        });

    /// <summary>
    /// <paramref name="artifact"/> is the setup token. It is claimed once, the access URL it returns
    /// is saved encrypted, and the first sync runs. A token the bridge refuses comes back as
    /// <c>Success = false</c> with a message the user can act on. If the first sync fails the
    /// connection is still saved (the token cannot be claimed twice) and the background loop retries.
    /// </summary>
    public async Task<AggregatorConnectResult> CompleteConnectionAsync(int userId, string artifact)
    {
        // A new token for a connection whose access is broken (login required or errored) repairs that
        // connection instead of adding a second one. A bank-level notice on a still-Active connection
        // is fixed at the bridge, so a token for that, or for anything else, is a new connection.
        // GetByUserIdAsync returns active rows only, so disconnected ones are never matched here.
        // The token names no connection, so with two broken ones there is no safe row to overwrite:
        // that is refused, before the token is spent, until the user removes the one they no longer use.
        var broken = (await _connections.GetByUserIdAsync(userId))
            .Where(c => c.Provider == BankProvider.SimpleFin && c.Status != PlaidItemStatus.Active)
            .ToList();
        if (broken.Count > 1)
        {
            return new AggregatorConnectResult
            {
                Provider = BankProvider.SimpleFin,
                Success = false,
                Message = "More than one SimpleFIN connection needs attention, so this token cannot be matched to one. Remove the ones you no longer use, then try again.",
            };
        }
        var existing = broken.FirstOrDefault();

        string accessUrl;
        try
        {
            accessUrl = await _client.ClaimAsync(artifact);
        }
        catch (SimpleFinException ex)
        {
            return new AggregatorConnectResult { Provider = BankProvider.SimpleFin, Success = false, Message = ex.Message };
        }

        var user = await _users.GetByIdAsync(userId);
        var now = DateTime.UtcNow;
        var itemId = SimpleFinMapping.ItemId(accessUrl);

        PlaidConnection connection;
        if (existing != null)
        {
            connection = existing;
            connection.AccessToken = _encryption.Encrypt(accessUrl);
            connection.ItemId = itemId;
            connection.IsActive = true;
            connection.DisconnectDate = null;
            ResetHealth(connection, now);
            await _connections.UpdateAsync(connection);
        }
        else
        {
            connection = await _connections.CreateAsync(new PlaidConnection
            {
                UserId = userId,
                FamilyId = user?.FamilyId,
                Provider = BankProvider.SimpleFin,
                AccessToken = _encryption.Encrypt(accessUrl),
                ItemId = itemId,
                InstitutionId = "simplefin",
                InstitutionName = "SimpleFIN",
                IsActive = true,
                Status = PlaidItemStatus.Active,
                ImportStatus = ImportStatus.InProgress,
                ImportStartedAt = now,
                LastSyncAt = now,
                // Marks the first sync as in flight so the background loop leaves this row alone.
                LastSyncAttemptAt = now,
            });
        }

        string? message = null;
        try
        {
            var from = now.AddDays(-Math.Max(1, _options.InitialImportDays));
            var count = await SyncCoreAsync(connection, from, now, nameConnection: true);

            connection = await _connections.GetByIdAsync(connection.Id) ?? connection;
            connection.ImportStatus = ImportStatus.Completed;
            connection.ImportProgress = 100;
            connection.ImportCompletedAt = now;
            connection.QuickSyncCompletedAt = now;
            connection.LastSyncAt = now;
            connection.LastTransactionSyncAt = now;
            connection.LastSyncTransactionCount = count;
            connection.LastSyncAttemptAt = now;
            connection.LastSyncError = null;
            await _connections.UpdateAsync(connection);
        }
        catch (Exception ex)
        {
            // The token is already spent, so keep the connection and let the loop try again.
            // LastTransactionSyncAt stays null, which makes the next loop sync fetch the full
            // initial window instead of the loop's usual short one.
            _logger.LogWarning(ex, "First SimpleFIN sync failed for connection {ConnectionId}", connection.Id);
            connection = await _connections.GetByIdAsync(connection.Id) ?? connection;
            connection.ImportStatus = ImportStatus.Failed;
            connection.LastSyncAttemptAt = now;
            connection.LastSyncError = UserText(ex);
            await _connections.UpdateAsync(connection);
            message = "Connected, but the first sync did not finish. It will be tried again later.";
        }

        return new AggregatorConnectResult
        {
            Provider = BankProvider.SimpleFin,
            Success = true,
            ExternalConnectionId = connection.ItemId,
            Message = message,
        };
    }

    public async Task<int> SyncConnectionTransactionsAsync(PlaidConnection connection, DateTime fromUtc, DateTime toUtc)
    {
        try
        {
            // No successful sync yet (the first one failed): backfill the whole initial window, since
            // the loop's own window is only a few days.
            var start = connection.LastTransactionSyncAt == null
                ? toUtc.AddDays(-Math.Max(1, _options.InitialImportDays))
                : fromUtc.AddDays(-ExtraOverlapDays);
            return await SyncCoreAsync(connection, start, toUtc, nameConnection: false);
        }
        catch (SimpleFinException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep database and parsing details out of what the user sees; the log has them.
            _logger.LogError(ex, "SimpleFIN sync failed for connection {ConnectionId}", connection.Id);
            throw new SimpleFinException("The sync failed. Check the server log for details.");
        }
    }

    public async Task DisconnectConnectionAsync(int userId, int connectionId)
    {
        var connection = await _connections.GetByIdAsync(connectionId);
        if (connection == null || connection.UserId != userId || connection.Provider != BankProvider.SimpleFin)
        {
            throw new ArgumentException("Connection not found or access denied");
        }

        // SimpleFIN has no call to revoke an access URL; the user does that at the bridge. What we
        // can do is forget it, so a stored secret does not outlive the connection.
        connection.AccessToken = string.Empty;
        connection.IsActive = false;
        connection.Status = PlaidItemStatus.Disconnected;
        connection.DisconnectDate = DateTime.UtcNow;
        connection.RequiresUserAction = false;
        connection.AlertSentAt = null;
        connection.LastStatusUpdate = DateTime.UtcNow;
        await _connections.UpdateAsync(connection);

        _logger.LogInformation("Disconnected SimpleFIN connection {ConnectionId} for user {UserId}", connectionId, userId);
    }

    // Nothing is held at the provider, so there is nothing to revoke.
    public Task<bool> TryRemoveConnectionAsync(PlaidConnection connection, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public async Task HandleSyncFailureAsync(int connectionId, string errorMessage)
    {
        try
        {
            var connection = await _connections.GetByIdAsync(connectionId);
            if (connection == null)
            {
                return;
            }

            var authFailure = errorMessage.Contains(SimpleFinAuthException.Marker, StringComparison.Ordinal);
            var shown = errorMessage.Replace($"{SimpleFinAuthException.Marker}: ", string.Empty);

            connection.LastSyncError = shown;
            connection.LastSyncAttemptAt = DateTime.UtcNow;
            connection.ConsecutiveFailures++;

            PlaidItemStatus? newStatus = authFailure
                ? PlaidItemStatus.LoginRequired
                : (connection.ConsecutiveFailures >= MaxConsecutiveSyncFailures ? PlaidItemStatus.Error : null);

            if (newStatus == null)
            {
                // A passing failure: note it and let the next attempt retry.
                await _connections.UpdateAsync(connection);
                _logger.LogWarning("SimpleFIN sync failure #{Failures} for connection {ConnectionId} (still retrying)",
                    connection.ConsecutiveFailures, connectionId);
                return;
            }

            connection.Status = newStatus.Value;
            connection.LastError = shown;
            connection.RequiresUserAction = true;
            connection.LastStatusUpdate = DateTime.UtcNow;
            await _connections.UpdateAsync(connection);

            await _alerts.NotifyNeedsAttentionAsync(connection, authFailure ? "ITEM_LOGIN_REQUIRED" : "SYNC_FAILED");

            _logger.LogWarning("SimpleFIN connection {ConnectionId} moved to {Status} after {Failures} failures",
                connectionId, newStatus.Value, connection.ConsecutiveFailures);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record a SimpleFIN sync failure for connection {ConnectionId}", connectionId);
        }
    }

    /// <summary>
    /// One pass over [from, to]: read the bridge (in 90-day slices), update accounts and balances,
    /// ingest the posted transactions. Pending transactions are skipped: the bridge gives them
    /// different ids from the posted charge and no link between the two, so keeping them would show
    /// every charge twice.
    /// </summary>
    private async Task<int> SyncCoreAsync(PlaidConnection connection, DateTime fromUtc, DateTime toUtc, bool nameConnection)
    {
        var accessUrl = _encryption.Decrypt(connection.AccessToken);
        if (string.IsNullOrEmpty(accessUrl))
        {
            throw new SimpleFinAuthException("This connection has no saved access. Paste a new setup token.");
        }

        var sets = new List<SimpleFinAccountSet>();
        foreach (var (from, to) in SimpleFinClient.SplitWindow(fromUtc, toUtc))
        {
            sets.Add(await _client.GetAccountsAsync(accessUrl, from, to));
        }
        if (sets.Count == 0)
        {
            return 0;
        }

        var user = await _users.GetByIdAsync(connection.UserId);

        // Slices run oldest first, so the last one carries the newest balances.
        var inScope = await UpsertAccountsAsync(connection, user, sets[^1].Accounts);

        var incoming = sets
            .SelectMany(s => s.Accounts)
            .Where(a => inScope.Contains(SimpleFinMapping.AccountId(connection.Id, a.Id)))
            .SelectMany(a => a.Transactions
                .Where(t => !t.Pending && t.Posted != null)
                .Select(t => SimpleFinMapping.ToIncoming(connection.Id, a, t)))
            .GroupBy(t => t.ExternalTransactionId)
            .Select(g => g.First())
            .ToList();

        var result = await _ingest.IngestAsync(incoming);

        if (nameConnection)
        {
            await NameConnectionAsync(connection.Id, sets[^1].Accounts);
        }

        var messages = sets.SelectMany(s => s.Messages).Distinct().ToList();
        await ReportBridgeMessagesAsync(connection.Id, messages, sets.Any(s => s.AuthProblem));

        return result.Created.Count;
    }

    /// <summary>Creates or updates the local accounts; returns the ids (as stored) of those in budget scope.</summary>
    private async Task<HashSet<string>> UpsertAccountsAsync(PlaidConnection connection, User? user, IReadOnlyList<SimpleFinAccount> accounts)
    {
        var inScope = new HashSet<string>(StringComparer.Ordinal);

        foreach (var account in accounts)
        {
            var storedId = SimpleFinMapping.AccountId(connection.Id, account.Id);
            var type = SimpleFinMapping.GuessAccountType(account.Name);
            var existing = await _accounts.GetByPlaidAccountIdAsync(storedId);

            // An id that already belongs to someone else's account is not ours to touch.
            if (existing != null && existing.UserId != connection.UserId)
            {
                continue;
            }

            if (!AccountTypePolicy.IsBudgetRelevant(type))
            {
                if (existing is { IsActive: true })
                {
                    existing.IsActive = false;
                    existing.UpdatedAt = DateTime.UtcNow;
                    await _accounts.UpdateAsync(existing);
                }
                _logger.LogInformation("Skipping out-of-scope SimpleFIN account for connection {ConnectionId}", connection.Id);
                continue;
            }

            // Accounts.Name and OfficialName hold 100 characters, and the bridge sets no limit.
            var name = SafeText.Truncate(account.Name.Length > 0 ? account.Name : account.OrgName, MaxAccountNameLength);
            var official = SafeText.Truncate(
                account.OrgName.Length > 0 && account.Name.Length > 0 ? $"{account.OrgName} {account.Name}" : name,
                MaxAccountNameLength);
            var current = SimpleFinMapping.StoredBalance(type, account.Balance ?? 0m);
            var available = account.AvailableBalance ?? (type == AccountType.CreditCard ? 0m : account.Balance ?? 0m);

            if (existing == null)
            {
                await _accounts.CreateAsync(new Account
                {
                    UserId = connection.UserId,
                    FamilyId = user?.FamilyId,
                    PlaidConnectionId = connection.Id,
                    PlaidAccountId = storedId,
                    Name = name,
                    OfficialName = official,
                    Type = type,
                    Subtype = string.Empty,
                    CurrentBalance = current,
                    AvailableBalance = available,
                    Currency = SimpleFinMapping.CurrencyOrDefault(account.Currency),
                    IsActive = true,
                });
            }
            else
            {
                existing.IsActive = true; // back in the bridge's list and in scope again
                existing.Name = name;
                existing.OfficialName = official;
                existing.PlaidConnectionId = connection.Id;
                existing.CurrentBalance = current;
                existing.AvailableBalance = available;
                if (existing.FamilyId == null && user?.FamilyId != null)
                {
                    existing.FamilyId = user.FamilyId;
                }
                await _accounts.UpdateAsync(existing);
            }

            inScope.Add(storedId);
        }

        // Accounts that dropped out of the bridge's list are no longer served: hide them rather
        // than leave stale balances. An empty list is treated as a bad read, not as "all removed".
        if (accounts.Count > 0)
        {
            var seen = accounts.Select(a => SimpleFinMapping.AccountId(connection.Id, a.Id)).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in (await _accounts.GetByConnectionIdAsync(connection.Id)).Where(a => !seen.Contains(a.PlaidAccountId)))
            {
                stale.IsActive = false;
                stale.UpdatedAt = DateTime.UtcNow;
                await _accounts.UpdateAsync(stale);
            }
        }

        return inScope;
    }

    // One bank: use its name. Several: say how many. The bridge does not tell us before the first read.
    private async Task NameConnectionAsync(int connectionId, IReadOnlyList<SimpleFinAccount> accounts)
    {
        var orgs = accounts.Select(a => a.OrgName).Where(o => o.Length > 0).Distinct().ToList();
        if (orgs.Count == 0)
        {
            return;
        }

        var connection = await _connections.GetByIdAsync(connectionId);
        if (connection == null)
        {
            return;
        }

        connection.InstitutionName = SafeText.Truncate(orgs.Count == 1 ? orgs[0] : $"SimpleFIN ({orgs.Count} banks)", 200);
        await _connections.UpdateAsync(connection);
    }

    /// <summary>
    /// The bridge can report a problem at one bank (it wants a fresh sign-in there) while the rest of
    /// the data is fine. That is fixed at the bridge, not with a new token, so the connection keeps
    /// syncing and is only flagged: the user is alerted once, and the flag clears when a read comes
    /// back clean.
    /// </summary>
    private async Task ReportBridgeMessagesAsync(int connectionId, List<string> messages, bool authProblem)
    {
        var connection = await _connections.GetByIdAsync(connectionId);
        if (connection == null)
        {
            return;
        }

        if (authProblem)
        {
            var text = messages.Count > 0 ? string.Join(" ", messages) : "SimpleFIN needs you to sign in at your bank again.";
            var alreadyFlagged = connection.RequiresUserAction;
            connection.LastError = SafeText.Truncate(text, 500);
            connection.RequiresUserAction = true;
            connection.LastStatusUpdate = DateTime.UtcNow;
            await _connections.UpdateAsync(connection);

            // Every read repeats the same message until it is fixed; alert on the first one only.
            if (!alreadyFlagged)
            {
                await _alerts.NotifyNeedsAttentionAsync(connection, "ITEM_LOGIN_REQUIRED");
            }
            return;
        }

        if (messages.Count > 0)
        {
            _logger.LogWarning("SimpleFIN reported {Count} message(s) for connection {ConnectionId}", messages.Count, connectionId);
        }

        if (connection.Status == PlaidItemStatus.Active && connection.RequiresUserAction)
        {
            connection.RequiresUserAction = false;
            connection.LastError = null;
            connection.LastStatusUpdate = DateTime.UtcNow;
            await _connections.UpdateAsync(connection);
        }
    }

    private static void ResetHealth(PlaidConnection connection, DateTime now)
    {
        connection.Status = PlaidItemStatus.Active;
        connection.RequiresUserAction = false;
        connection.LastError = null;
        connection.LastSyncError = null;
        connection.ConsecutiveFailures = 0;
        connection.AlertSentAt = null;
        connection.LastStatusUpdate = now;
    }

    // Only our own exceptions carry text written for the user.
    private static string UserText(Exception ex) =>
        ex is SimpleFinException ? ex.Message.Replace($"{SimpleFinAuthException.Marker}: ", string.Empty) : "The first sync failed.";
}
