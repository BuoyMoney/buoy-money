using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services.Bridge;

/// <summary>A bridge sync failed in a way the user can be told about. The message is safe to show.</summary>
public class BridgeSyncException(string message) : Exception(message);

/// <summary>The bank needs the person to sign in again. The marker lets the failure handler tell this from a passing error.</summary>
public sealed class BridgeLoginException : BridgeSyncException
{
  public const string Marker = "BRIDGE_AUTH";

  public BridgeLoginException(string message) : base($"{Marker}: {message}") { }
}

/// <summary>
/// The Cloud is throttling this server or the bank is still loading. Not a fault: the failure handler waits instead of
/// counting it. The message carries how long to wait, when the Cloud said.
/// </summary>
public sealed class BridgeRetryLaterException : BridgeSyncException
{
  public const string Marker = "BRIDGE_RETRY";

  public BridgeRetryLaterException(string message, TimeSpan? retryAfter)
    : base($"{Marker}[{(int)Math.Clamp(retryAfter?.TotalSeconds ?? 0, 0, 86400)}]: {message}") { }
}

/// <summary>
/// The BuoyAI Cloud bank feed as a third <see cref="IBankAggregator"/> (Plaid behind the Cloud bridge, for self-hosted
/// servers). The Cloud holds the Plaid login; this server holds a pairing and receives encrypted batches. A connection row
/// carries the Cloud's opaque id for the bank in <c>ItemId</c> and no secret at all.
///
/// A sync asks the Cloud which banks have news (a cheap call that makes no Plaid request), pulls a batch only for those,
/// stores it, and only then acknowledges it. A batch that was applied but not acknowledged comes back on the next sync and
/// is skipped by the ingest dedupe, so a crash between the two steps loses nothing and doubles nothing.
/// </summary>
public class BridgeAggregator : IBankAggregator
{
  private const int MaxConsecutiveSyncFailures = 4;

  private static readonly System.Text.RegularExpressions.Regex RetryLater =
    new(BridgeRetryLaterException.Marker + @"\[(\d+)\]: (.*)", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

  private readonly BridgeClient _client;
  private readonly CloudBridgeOptions _options;
  private readonly IPlaidConnectionRepository _connections;
  private readonly IAccountRepository _accounts;
  private readonly IUserRepository _users;
  private readonly ITransactionRepository _transactions;
  private readonly ITransactionIngestService _ingest;
  private readonly IBankConnectionAlertService _alerts;
  private readonly BridgePendingLinks _pending;
  private readonly ILogger<BridgeAggregator> _logger;

  public BridgeAggregator(
    BridgeClient client,
    IOptions<CloudBridgeOptions> options,
    IPlaidConnectionRepository connections,
    IAccountRepository accounts,
    IUserRepository users,
    ITransactionRepository transactions,
    ITransactionIngestService ingest,
    IBankConnectionAlertService alerts,
    BridgePendingLinks pending,
    ILogger<BridgeAggregator> logger)
  {
    _client = client;
    _options = options.Value;
    _connections = connections;
    _accounts = accounts;
    _users = users;
    _transactions = transactions;
    _ingest = ingest;
    _alerts = alerts;
    _pending = pending;
    _logger = logger;
  }

  public BankProvider Provider => BankProvider.Bridge;

  public TimeSpan MinimumSyncInterval => TimeSpan.FromMinutes(Math.Max(0, _options.MinSyncIntervalMinutes));

  public TimeSpan SyncJitter => TimeSpan.FromMinutes(Math.Max(0, _options.JitterMinutes));

  /// <summary>Asks the Cloud for a one-time page address. The person opens it, agrees, and signs in at their bank there.</summary>
  public Task<AggregatorConnectStart> StartConnectionAsync(int userId, string? platform) => StartAsync(userId, null, null);

  /// <summary>Starts the Cloud page that signs in again for a bank that needs it. The same bank is kept; no second login is made.</summary>
  public async Task<AggregatorConnectStart> StartReconnectAsync(int userId, int connectionId, string? platform)
  {
    var connection = await _connections.GetByIdAsync(connectionId);
    if (connection == null || connection.UserId != userId || connection.Provider != BankProvider.Bridge
        || !BridgeMapping.TryParseItemId(connection.ItemId, out var itemId))
      throw new DomainValidationException("Connection not found.");
    return await StartAsync(userId, itemId, connectionId);
  }

  private async Task<AggregatorConnectStart> StartAsync(int userId, Guid? reconnectItemId, int? connectionId)
  {
    try
    {
      var link = await _client.StartLinkAsync(reconnectItemId) ?? throw new DomainValidationException("The Cloud did not start a bank connection.");
      var expires = DateTime.SpecifyKind(link.ExpiresAt, DateTimeKind.Utc);
      // The Cloud keys banks by server, so this server remembers whose page this is.
      _pending.Add(new PendingBridgeLink(link.SessionId, userId, connectionId, expires));
      return new AggregatorConnectStart
      {
        Provider = BankProvider.Bridge,
        Kind = AggregatorConnectKind.RedirectUrl,
        Value = link.Url,
        ExpiresAt = expires
      };
    }
    catch (BridgeCloudException ex)
    {
      throw new DomainValidationException(ex.Message);
    }
  }

  private const string NothingYet = "No new bank connection has finished yet. If you just signed in at your bank, wait a moment and try again.";

  /// <summary>
  /// Called when the person comes back from the Cloud page. There is no token to hand over: this server asks the Cloud which
  /// bank the page this user started made, and adopts that one bank for that user. Banks made by other pages (another
  /// user's, or none of this server's users) are never touched. <paramref name="artifact"/> may be the page address or its
  /// session id; when it is neither, the user's most recent page is used.
  /// </summary>
  public async Task<AggregatorConnectResult> CompleteConnectionAsync(int userId, string artifact)
  {
    var pending = _pending.Find(userId, SessionIdFrom(artifact)) ?? _pending.Find(userId, null);
    if (pending == null) return Failed(NothingYet);

    BridgeLinkResult? link;
    IReadOnlyList<BridgeItem> items;
    try
    {
      link = await _client.GetLinkResultAsync(pending.SessionId);
      items = link?.ItemId == null ? [] : await _client.ListItemsAsync();
    }
    catch (BridgeCloudException ex)
    {
      if (ex.Status == 404) _pending.Remove(pending.SessionId);
      return Failed(ex.Message);
    }

    if (link?.Status is "failed" or "expired")
    {
      _pending.Remove(pending.SessionId);
      return Failed("That bank connection did not finish. Start again to connect your bank.");
    }
    if (link?.Status != "finished" || link.ItemId == null) return Failed(NothingYet);

    var item = items.FirstOrDefault(i => i.Id == link.ItemId);
    if (item == null) return Failed("The Cloud no longer has this bank connection. Start again to connect your bank.");

    var result = pending.ConnectionId is { } connectionId
      ? await ReactivateAsync(userId, connectionId, item)
      : await AdoptAsync(userId, item);
    if (result.Success) _pending.Remove(pending.SessionId);
    return result;
  }

  private static AggregatorConnectResult Failed(string message) =>
    new() { Provider = BankProvider.Bridge, Success = false, Message = message };

  private static string? SessionIdFrom(string artifact)
  {
    var text = artifact.Trim();
    var at = text.LastIndexOf("/link/", StringComparison.Ordinal);
    if (at >= 0) text = text[(at + "/link/".Length)..];
    var end = text.IndexOfAny(['/', '?', '#']);
    return end >= 0 ? text[..end] : text;
  }

  /// <summary>Makes a connection for a bank the Cloud just made for this user, and imports it.</summary>
  private async Task<AggregatorConnectResult> AdoptAsync(int userId, BridgeItem item)
  {
    var itemId = BridgeMapping.ItemId(item.Id);
    if (await _connections.GetByItemIdAsync(itemId) != null) return Failed(NothingYet);

    var user = await _users.GetByIdAsync(userId);
    var now = DateTime.UtcNow;
    string? message = null;

    var connection = await _connections.CreateAsync(new PlaidConnection
    {
      UserId = userId,
      FamilyId = user?.FamilyId,
      Provider = BankProvider.Bridge,
      AccessToken = string.Empty,
      ItemId = itemId,
      InstitutionId = "bridge",
      InstitutionName = SafeText.Truncate(string.IsNullOrWhiteSpace(item.InstitutionName) ? "Bank" : item.InstitutionName, 200),
      IsActive = true,
      Status = item.Status == "active" ? PlaidItemStatus.Active : PlaidItemStatus.LoginRequired,
      ImportStatus = ImportStatus.InProgress,
      ImportStartedAt = now,
      LastSyncAt = now,
      LastSyncAttemptAt = now,
    });

    try
    {
      var count = await SyncCoreAsync(connection, item);
      connection = await _connections.GetByIdAsync(connection.Id) ?? connection;
      connection.ImportStatus = ImportStatus.Completed;
      connection.ImportProgress = 100;
      connection.ImportCompletedAt = now;
      connection.QuickSyncCompletedAt = now;
      connection.LastSyncAt = now;
      connection.LastTransactionSyncAt = now;
      connection.LastSyncTransactionCount = count;
      connection.LastSyncError = null;
      await _connections.UpdateAsync(connection);
    }
    catch (Exception ex) when (ex is BridgeSyncException or BridgeCloudException or CryptographicException)
    {
      // The bank is already connected at the Cloud, so keep the row and let the loop try again.
      _logger.LogWarning("First bridge sync failed for connection {ConnectionId}: {Kind}", connection.Id, ex.GetType().Name);
      connection = await _connections.GetByIdAsync(connection.Id) ?? connection;
      connection.ImportStatus = ImportStatus.Failed;
      connection.LastSyncError = UserText(ex);
      await _connections.UpdateAsync(connection);
      message = "Connected, but the first sync did not finish. It will be tried again later.";
    }

    return new AggregatorConnectResult { Provider = BankProvider.Bridge, Success = true, ExternalConnectionId = connection.ItemId, Message = message };
  }

  /// <summary>The person signed in again: the connection goes back to active and syncs.</summary>
  private async Task<AggregatorConnectResult> ReactivateAsync(int userId, int connectionId, BridgeItem item)
  {
    var connection = await _connections.GetByIdAsync(connectionId);
    if (connection == null || connection.UserId != userId || connection.Provider != BankProvider.Bridge
        || connection.ItemId != BridgeMapping.ItemId(item.Id))
      return Failed("Connection not found.");

    var now = DateTime.UtcNow;
    connection.Status = PlaidItemStatus.Active;
    connection.IsActive = true;
    connection.RequiresUserAction = false;
    connection.ConsecutiveFailures = 0;
    connection.AlertSentAt = null;
    connection.LastError = null;
    connection.LastSyncError = null;
    connection.LastStatusUpdate = now;
    connection.LastSyncAttemptAt = now;
    connection.ExpirationDate = null;
    if (item.NoticeAt is { } noticeAt) connection.BridgeNoticeAt = noticeAt;
    await _connections.UpdateAsync(connection);
    await _alerts.NotifyResolvedAsync(connection);

    string? message = null;
    try
    {
      var count = await SyncCoreAsync(connection, item);
      connection = await _connections.GetByIdAsync(connection.Id) ?? connection;
      connection.LastSyncAt = now;
      connection.LastTransactionSyncAt = now;
      connection.LastSyncTransactionCount = count;
      await _connections.UpdateAsync(connection);
    }
    catch (Exception ex) when (ex is BridgeSyncException or BridgeCloudException or CryptographicException)
    {
      _logger.LogWarning("Sync after a bridge reconnect failed for connection {ConnectionId}: {Kind}", connection.Id, ex.GetType().Name);
      message = "Reconnected, but the sync did not finish. It will be tried again later.";
    }
    return new AggregatorConnectResult { Provider = BankProvider.Bridge, Success = true, ExternalConnectionId = connection.ItemId, Message = message };
  }

  public async Task<int> SyncConnectionTransactionsAsync(PlaidConnection connection, DateTime fromUtc, DateTime toUtc)
  {
    try
    {
      if (!BridgeMapping.TryParseItemId(connection.ItemId, out var itemId))
        throw new BridgeSyncException("This connection is not a Cloud bank connection.");
      var items = await _client.ListItemsAsync();
      var item = items.FirstOrDefault(i => i.Id == itemId)
                 ?? throw new BridgeLoginException("The Cloud no longer has this bank connection. Connect the bank again.");
      await ApplyCloudStateAsync(connection, item);
      return await SyncCoreAsync(connection, item);
    }
    catch (BridgeSyncException)
    {
      throw;
    }
    catch (BridgeCloudException ex) when (ex.RetryLater)
    {
      throw new BridgeRetryLaterException(ex.Message, ex.RetryAfter);
    }
    catch (BridgeCloudException ex)
    {
      throw ex.CredentialRejected
        ? new BridgeLoginException(ex.Message)
        : ex.NeedsUserAtBank ? new BridgeLoginException(ex.Message) : new BridgeSyncException(ex.Message);
    }
    catch (CryptographicException)
    {
      throw new BridgeSyncException("A batch from the Cloud could not be opened with this server's key. Pair this server with the Cloud again.");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Bridge sync failed for connection {ConnectionId}", connection.Id);
      throw new BridgeSyncException("The sync failed. Check the server log for details.");
    }
  }

  /// <summary>
  /// One list call for all the connections waiting for a sign-in. A bank the Cloud now reports as healthy (the person fixed it
  /// at the bank, and the Cloud saw LOGIN_REPAIRED) goes back to active with its notification resolved; nothing else changes.
  /// The attempt time is stamped so the sync schedule's interval also paces these calls.
  /// </summary>
  public async Task ReconcileConnectionsAsync(IReadOnlyCollection<PlaidConnection> connections)
  {
    try
    {
      if (connections.Count == 0) return;
      var items = await _client.ListItemsAsync();
      foreach (var connection in connections)
      {
        try
        {
          var saved = false;
          if (BridgeMapping.TryParseItemId(connection.ItemId, out var itemId) && items.FirstOrDefault(i => i.Id == itemId) is { } item)
          {
            saved = await ApplyCloudStateAsync(connection, item);
          }
          if (!saved)
          {
            // Stamped even when the Cloud no longer lists the item, so the pacing holds and it is asked once per interval.
            connection.LastSyncAttemptAt = DateTime.UtcNow;
            await _connections.UpdateAsync(connection);
          }
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "Could not reconcile bridge connection {ConnectionId}", connection.Id);
        }
      }
    }
    catch (Exception ex)
    {
      // The Cloud is unreachable or refused this server; the connections stay as they are and are asked again next interval.
      _logger.LogInformation("Could not ask the Cloud about {Count} bridge connections: {Kind}", connections.Count, ex.GetType().Name);
    }
  }

  /// <summary>
  /// Puts a connection's status in line with the Cloud's view of its bank, and alerts about a new heads-up. A bank the Cloud
  /// still has as login_required or revoked is left to the sync path, which reports it. Returns true when the connection was saved.
  /// </summary>
  private async Task<bool> ApplyCloudStateAsync(PlaidConnection connection, BridgeItem item)
  {
    if (item.Status != "active" || connection.Status is not (PlaidItemStatus.Active or PlaidItemStatus.LoginRequired
        or PlaidItemStatus.PendingExpiration or PlaidItemStatus.PendingDisconnect))
      return false;

    var now = DateTime.UtcNow;
    var desired = item.Notice switch
    {
      "pending_expiration" => PlaidItemStatus.PendingExpiration,
      "pending_disconnect" => PlaidItemStatus.PendingDisconnect,
      _ => PlaidItemStatus.Active
    };
    var changed = false;
    var resolved = false;

    if (connection.Status != desired)
    {
      resolved = desired == PlaidItemStatus.Active;
      connection.Status = desired;
      connection.RequiresUserAction = desired != PlaidItemStatus.Active;
      connection.LastStatusUpdate = now;
      if (desired == PlaidItemStatus.Active)
      {
        connection.ConsecutiveFailures = 0;
        connection.AlertSentAt = null;
        connection.LastError = null;
        connection.ExpirationDate = null;
      }
      else if (desired == PlaidItemStatus.PendingExpiration)
      {
        // The same default the Plaid webhook uses: Plaid gives about a week.
        connection.ExpirationDate = now.AddDays(7);
      }
      changed = true;
    }

    // The Cloud keeps a heads-up until a reconnect clears it; alert once per heads-up.
    string? reason = null;
    if (item.Notice != null && item.NoticeAt is { } noticeAt && (connection.BridgeNoticeAt == null || noticeAt > connection.BridgeNoticeAt))
    {
      connection.BridgeNoticeAt = noticeAt;
      reason = item.Notice switch
      {
        "pending_expiration" => "PENDING_EXPIRATION",
        "pending_disconnect" => "PENDING_DISCONNECT",
        "new_accounts_available" => "NEW_ACCOUNTS_AVAILABLE",
        _ => null
      };
      changed = true;
    }

    if (!changed) return false;
    connection.LastSyncAttemptAt = now;
    await _connections.UpdateAsync(connection);
    if (resolved) await _alerts.NotifyResolvedAsync(connection);
    if (reason != null) await _alerts.NotifyNeedsAttentionAsync(connection, reason);
    _logger.LogInformation("Bridge connection {ConnectionId} is now {Status} (Cloud notice: {Notice})", connection.Id, connection.Status, item.Notice ?? "none");
    return true;
  }

  public async Task DisconnectConnectionAsync(int userId, int connectionId)
  {
    var connection = await _connections.GetByIdAsync(connectionId);
    if (connection == null || connection.UserId != userId || connection.Provider != BankProvider.Bridge)
      throw new ArgumentException("Connection not found or access denied");

    // Removing the bank at the Cloud ends its monthly charge at Plaid, so a failure we can retry is shown, not hidden.
    if (!await TryRemoveConnectionAsync(connection))
      throw new DomainValidationException("Could not reach the Cloud to remove this bank. Try again in a moment.");

    connection.AccessToken = string.Empty;
    connection.IsActive = false;
    connection.Status = PlaidItemStatus.Disconnected;
    connection.DisconnectDate = DateTime.UtcNow;
    connection.RequiresUserAction = false;
    connection.AlertSentAt = null;
    connection.LastStatusUpdate = DateTime.UtcNow;
    await _connections.UpdateAsync(connection);
    _logger.LogInformation("Disconnected bridge connection {ConnectionId} for user {UserId}", connectionId, userId);
  }

  public async Task<bool> TryRemoveConnectionAsync(PlaidConnection connection, CancellationToken cancellationToken = default)
  {
    if (!BridgeMapping.TryParseItemId(connection.ItemId, out var itemId)) return true;
    try
    {
      await _client.RemoveItemAsync(itemId, cancellationToken);
      return true;
    }
    catch (BridgeCloudException ex) when (ex.Status is 404 or 401)
    {
      // Already gone at the Cloud, or the Cloud no longer accepts this server (its sweep removes orphans).
      return true;
    }
    catch (BridgeCloudException ex)
    {
      _logger.LogWarning("Could not remove bridge connection {ConnectionId}: {Error}", connection.Id, ex.Error);
      return false;
    }
  }

  public async Task HandleSyncFailureAsync(int connectionId, string errorMessage)
  {
    try
    {
      var connection = await _connections.GetByIdAsync(connectionId);
      if (connection == null) return;

      var retry = RetryLater.Match(errorMessage);
      if (retry.Success)
      {
        // A throttle or a bank still loading: wait, as the Cloud asked, and do not count it against the connection.
        var wait = TimeSpan.FromSeconds(int.Parse(retry.Groups[1].Value));
        connection.LastSyncError = retry.Groups[2].Value;
        connection.LastSyncAttemptAt = DateTime.UtcNow + (wait > MinimumSyncInterval ? wait - MinimumSyncInterval : TimeSpan.Zero);
        await _connections.UpdateAsync(connection);
        _logger.LogInformation("Bridge sync for connection {ConnectionId} deferred by the Cloud for {Seconds}s", connectionId, (int)wait.TotalSeconds);
        return;
      }

      var loginProblem = errorMessage.Contains(BridgeLoginException.Marker, StringComparison.Ordinal);
      var shown = errorMessage.Replace($"{BridgeLoginException.Marker}: ", string.Empty);

      connection.LastSyncError = shown;
      connection.LastSyncAttemptAt = DateTime.UtcNow;
      connection.ConsecutiveFailures++;

      PlaidItemStatus? newStatus = loginProblem
        ? PlaidItemStatus.LoginRequired
        : (connection.ConsecutiveFailures >= MaxConsecutiveSyncFailures ? PlaidItemStatus.Error : null);

      if (newStatus == null)
      {
        await _connections.UpdateAsync(connection);
        _logger.LogWarning("Bridge sync failure #{Failures} for connection {ConnectionId} (still retrying)", connection.ConsecutiveFailures, connectionId);
        return;
      }

      connection.Status = newStatus.Value;
      connection.LastError = shown;
      connection.RequiresUserAction = true;
      connection.LastStatusUpdate = DateTime.UtcNow;
      await _connections.UpdateAsync(connection);

      await _alerts.NotifyNeedsAttentionAsync(connection, loginProblem ? "ITEM_LOGIN_REQUIRED" : "SYNC_FAILED");
      _logger.LogWarning("Bridge connection {ConnectionId} moved to {Status} after {Failures} failures", connectionId, newStatus.Value, connection.ConsecutiveFailures);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Could not record a bridge sync failure for connection {ConnectionId}", connectionId);
    }
  }

  /// <summary>One pass: if the Cloud says the bank has news, pull, apply and acknowledge batches until it has no more.</summary>
  private async Task<int> SyncCoreAsync(PlaidConnection connection, BridgeItem item)
  {
    if (item.Status != "active")
      throw new BridgeLoginException(item.Status == "revoked"
        ? "Access to this bank was revoked. Connect the bank again."
        : "Your bank needs you to sign in again.");
    if (item.OverCap)
      throw new BridgeSyncException("This account is over its bank login limit, so this bank is not synced. Remove a bank or add logins in your Cloud account.");

    // The first sync of a connection always pulls; after that only when the Cloud says there is something.
    if (!item.UpdatesAvailable && connection.LastTransactionSyncAt != null) return 0;

    var total = 0;
    for (var i = 0; i < Math.Max(1, _options.MaxBatchesPerSync); i++)
    {
      BridgeBatch? batch;
      try
      {
        batch = await _client.PullAsync(item.Id);
      }
      catch (BridgeCloudException ex) when (ex.RetryLater && i > 0)
      {
        // Throttled part way through a long catch-up. Everything applied has been acknowledged, and the Cloud keeps the
        // "more to come" flag, so the next sync carries on from here.
        _logger.LogInformation("Cloud throttled a bridge catch-up after {Batches} batches; it will continue next sync", i);
        break;
      }
      if (batch == null) break;

      var body = await _client.OpenBatchAsync(batch);
      total += await ApplyAsync(connection, body);
      await _client.AckAsync(batch.BatchId);
      if (!batch.HasMore) break;
    }
    return total;
  }

  private async Task<int> ApplyAsync(PlaidConnection connection, JsonObject body)
  {
    var user = await _users.GetByIdAsync(connection.UserId);
    var inScope = await UpsertAccountsAsync(connection, user, body["accounts"] as JsonArray);

    var incoming = new List<IncomingTransaction>();
    foreach (var node in (body["added"] as JsonArray ?? []).Concat(body["modified"] as JsonArray ?? []))
    {
      if (node == null) continue;
      var tx = BridgeMapping.ToIncoming(node);
      if (tx.ExternalAccountId == null || !inScope.Contains(tx.ExternalAccountId)) continue;
      incoming.Add(tx);
    }
    var created = 0;
    if (incoming.Count > 0)
    {
      // Distinct on the id: a transaction can be in both lists. Ingest skips ones already stored, so a modified row that
      // we have is left as it was (the ingest seam only creates), and a replayed batch changes nothing.
      var result = await _ingest.IngestAsync(incoming.GroupBy(t => t.ExternalTransactionId).Select(g => g.First()).ToList());
      created = result.Created.Count;
    }

    await RemoveTransactionsAsync(body["removed"] as JsonArray);

    var institution = body["institution_name"]?.GetValue<string>();
    if (!string.IsNullOrWhiteSpace(institution) && connection.InstitutionName != institution)
    {
      var fresh = await _connections.GetByIdAsync(connection.Id);
      if (fresh != null && fresh.InstitutionName is "" or "Bank")
      {
        fresh.InstitutionName = SafeText.Truncate(institution, 200);
        await _connections.UpdateAsync(fresh);
      }
    }
    return created;
  }

  private async Task RemoveTransactionsAsync(JsonArray? removed)
  {
    if (removed == null) return;
    foreach (var node in removed)
    {
      var raw = node?["transaction_id"]?.GetValue<string>();
      if (string.IsNullOrEmpty(raw)) continue;
      var transaction = await _transactions.GetByPlaidTransactionIdAsync(BridgeMapping.TransactionId(raw));
      if (transaction == null) continue;
      // Same rule as Plaid: a row the user has worked on is kept for them to review.
      if (!PlaidService.IsUntouchedByUser(transaction)) continue;
      await _transactions.DeleteAsync(transaction.Id);
    }
  }

  /// <summary>Creates or updates the local accounts; returns the ids (as stored) of those in budget scope.</summary>
  private async Task<HashSet<string>> UpsertAccountsAsync(PlaidConnection connection, User? user, JsonArray? accounts)
  {
    var inScope = new HashSet<string>(StringComparer.Ordinal);
    if (accounts == null) return inScope;

    foreach (var account in accounts)
    {
      var rawId = account?["account_id"]?.GetValue<string>();
      if (account == null || string.IsNullOrWhiteSpace(rawId)) continue;

      var storedId = BridgeMapping.AccountId(rawId);
      var type = BridgeMapping.MapAccountType(account["type"]?.GetValue<string>(), account["subtype"]?.GetValue<string>());
      var existing = await _accounts.GetByPlaidAccountIdAsync(storedId);

      // An id that already belongs to someone else's account is not ours to touch.
      if (existing != null && existing.UserId != connection.UserId) continue;

      if (!AccountTypePolicy.IsBudgetRelevant(type))
      {
        if (existing is { IsActive: true })
        {
          existing.IsActive = false;
          existing.UpdatedAt = DateTime.UtcNow;
          await _accounts.UpdateAsync(existing);
        }
        continue;
      }

      var name = account["name"]?.GetValue<string>() is { Length: > 0 } n ? n : "Account";
      var official = account["official_name"]?.GetValue<string>() is { Length: > 0 } o ? o : name;
      var balances = account["balances"];
      var current = BridgeMapping.Balance(balances?["current"]) ?? 0m;
      var available = BridgeMapping.Balance(balances?["available"]) ?? (type == AccountType.CreditCard ? 0m : current);
      var currency = BridgeMapping.CurrencyOrDefault(balances?["iso_currency_code"]?.GetValue<string>());
      var mask = account["mask"]?.GetValue<string>();
      var subtype = account["subtype"]?.GetValue<string>() ?? string.Empty;

      if (existing == null)
      {
        await _accounts.CreateAsync(new Account
        {
          UserId = connection.UserId,
          FamilyId = user?.FamilyId,
          PlaidConnectionId = connection.Id,
          PlaidAccountId = storedId,
          Name = SafeText.Truncate(name, 200),
          OfficialName = SafeText.Truncate(official, 200),
          Mask = mask,
          Type = type,
          Subtype = SafeText.Truncate(subtype, 50),
          CurrentBalance = current,
          AvailableBalance = available,
          Currency = currency,
          IsActive = true,
        });
      }
      else
      {
        existing.Name = SafeText.Truncate(name, 200);
        existing.OfficialName = SafeText.Truncate(official, 200);
        existing.PlaidConnectionId = connection.Id;
        existing.CurrentBalance = current;
        existing.AvailableBalance = available;
        if (existing.FamilyId == null && user?.FamilyId != null) existing.FamilyId = user.FamilyId;
        await _accounts.UpdateAsync(existing);
      }
      inScope.Add(storedId);
    }
    return inScope;
  }

  // Only our own exceptions carry text written for the user.
  private static string UserText(Exception ex) =>
    ex is BridgeSyncException or BridgeCloudException ? ex.Message.Replace($"{BridgeLoginException.Marker}: ", string.Empty) : "The first sync failed.";
}
