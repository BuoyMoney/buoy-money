using Going.Plaid.Accounts;
using Going.Plaid.Transactions;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.ValueObjects;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Partial class containing transaction and account sync methods:
/// - Account fetching and synchronization
/// - Transaction fetching and synchronization
/// - Historical data import
/// - Pending transaction cleanup
/// </summary>
public partial class PlaidService
{
  public async Task<IEnumerable<AccountDto>> GetAccountsAsync(int userId)
  {
    try
    {
      // Family-aware (parity with the transactions view): show the whole family's accounts, not
      // just the user's own. GetByUserOrFamilyAsync handles a null FamilyId (solo user) — it then
      // resolves to just the user's accounts.
      var user = await _userRepository.GetByIdAsync(userId);
      var accounts = await _accountRepository.GetByUserOrFamilyAsync(userId, user?.FamilyId);

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

  public async Task<IEnumerable<TransactionDto>> GetTransactionsAsync(int userId, DateTime? startDate = null, DateTime? endDate = null)
  {
    try
    {
      var transactions = await _transactionRepository.GetByUserIdAsync(
        userId,
        startDate ?? DateTime.UtcNow.AddMonths(-1),
        endDate ?? DateTime.UtcNow
      );

      return transactions.Select(t => new TransactionDto
      {
        Id = t.Id,
        AccountId = t.AccountId,
        PlaidTransactionId = t.PlaidTransactionId,
        Amount = t.Amount,
        Type = t.Type,
        IsoCurrencyCode = t.IsoCurrencyCode,
        Description = t.Description,
        MerchantName = t.MerchantName,
        Nickname = t.Nickname,
        DisplayName = t.DisplayName,
        CategoryId = t.CategoryId,
        Date = t.Date,
        IsPending = t.IsPending,
        IsUserCategorized = t.IsUserCategorized
      });
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting transactions for user {UserId}", userId);
      throw;
    }
  }

  public async Task<TransactionSyncResult> SyncTransactionsAsync(int userId)
  {
    var result = new TransactionSyncResult();
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    // Emit a domain span so the slowest user-facing flow (Plaid sync) is attributable in
    // traces beyond the aggregate buoyai.plaid.sync.duration histogram.
    using var activity = Telemetry.BuoyAiActivitySource.StartActivity("plaid.sync");
    activity?.SetTag("user_id", userId);

    try
    {
      // Plaid only: another provider's connections hold a different kind of secret and are
      // synced by their own aggregator.
      var connections = (await _plaidConnectionRepository.GetByUserIdAsync(userId))
        .Where(c => c.Provider == BankProvider.Plaid)
        .ToList();
      activity?.SetTag("connection_count", connections.Count);
      
      foreach (var connection in connections)
      {
        var accessToken = _encryptionService.Decrypt(connection.AccessToken);
        
        // Sync accounts first to update balances, mask, and other account metadata
        await SyncAccountsAsync(userId, accessToken);
        
        // Then sync transactions; rules and AI queueing already ran on the new rows
        var connectionResult = await SyncTransactionsForConnectionAsync(connection, accessToken);

        result.TotalNewTransactions += connectionResult.TotalNewTransactions;
        result.TransactionsNeedingAI += connectionResult.TransactionsNeedingAI;
        result.RulesApplied += connectionResult.RulesApplied;
        result.QueuedTransactionIds.AddRange(connectionResult.QueuedTransactionIds);
        
        connection.LastSyncAt = DateTime.UtcNow;
        await _plaidConnectionRepository.UpdateAsync(connection);
      }

      // Reconcile pending holds against their posted charges so users never see a
      // hold + its final charge as two rows (handles institutions that don't link
      // them via pending_transaction_id).
      await CleanupStalePendingTransactionsAsync(userId);

      stopwatch.Stop();
      _metrics.RecordPlaidSync(result.TotalNewTransactions);
      _metrics.RecordPlaidSyncDuration(stopwatch.Elapsed.TotalSeconds);

      return result;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error syncing transactions for user {UserId}", userId);
      throw;
    }
  }

  /// <summary>
  /// Fetches ALL transactions for the given access token and date window, paging through
  /// Plaid's /transactions/get endpoint. A single /transactions/get call returns at most
  /// one page (Plaid caps <c>count</c> at 500, and honors a lower/explicit value); the
  /// response reports the full match count in <c>TotalTransactions</c>. This helper keeps
  /// fetching with an increasing <c>offset</c> until it has accumulated every transaction
  /// Plaid reports, so callers never silently truncate at the first page.
  ///
  /// Returns the FIRST page's response (which carries the account balances callers persist)
  /// together with the complete, accumulated transaction list across all pages. The
  /// <paramref name="execute"/> delegate lets each call site route the request through its
  /// own rate-limiting wrapper — or issue a direct client call. When <paramref name="itemId"/>
  /// is supplied, each page call is additionally wrapped in <see cref="ExecuteWithRateLimit"/>
  /// so bulk paths (e.g. historical import) get 429 backoff; when null, calls go straight
  /// through <paramref name="execute"/> (preserving existing behavior).
  /// </summary>
  internal async Task<(TransactionsGetResponse? firstPage, List<Going.Plaid.Entity.Transaction> transactions)>
    FetchAllTransactionsAsync(
      string accessToken,
      DateOnly startDate,
      DateOnly endDate,
      Func<TransactionsGetRequest, Task<TransactionsGetResponse>> execute,
      string? itemId = null)
  {
    // Plaid allows up to 500 per page for /transactions/get; larger pages mean fewer
    // round-trips (and fewer rate-limited calls) to pull a full window.
    const int pageSize = 500;
    TransactionsGetResponse? firstPage = null;
    var allTransactions = new List<Going.Plaid.Entity.Transaction>();

    while (true)
    {
      var request = new TransactionsGetRequest
      {
        AccessToken = accessToken,
        StartDate = startDate,
        EndDate = endDate,
        Options = new Going.Plaid.Entity.TransactionsGetRequestOptions
        {
          Count = pageSize,
          Offset = allTransactions.Count,
          // Ask for the institution's raw description. Plaid withholds it unless this is set,
          // which is why Transaction.OriginalDescription was null on every row we had — the
          // original `MerchantName ?? OriginalDescription` chain was Plaid's recommended
          // pairing, it just never opted in, so it always collapsed to empty.
          IncludeOriginalDescription = true
        }
      };

      var response = itemId != null
        ? await ExecuteWithRateLimit(
            itemId,
            () => execute(request),
            _plaidUsageTracker.CanMakeRefreshRequest,
            _plaidUsageTracker.RecordRefreshRequest,
            "TransactionsGet")
        : await execute(request);
      if (response == null)
      {
        break;
      }

      // Going.Plaid does NOT throw on Plaid API errors: an ITEM_LOGIN_REQUIRED (or any other
      // item error) comes back as a normal response with Error != null and Transactions == null.
      // Left unhandled the loop would hit a null page, break, and return 0 transactions with no
      // exception — the background sync then runs its SUCCESS branch and never classifies the
      // failure, so a broken item stays Active and is re-synced forever. Surface it as an
      // exception carrying the raw Plaid error CODE (ErrorCode, not just the message) so the
      // sync path routes it through HandleSyncFailureAsync and LoginRequiredErrorCodes matches,
      // flipping the connection to LoginRequired/Error. Genuinely-empty successful pages
      // (Error == null, empty list) fall through unchanged.
      if (response.Error != null)
      {
        throw new InvalidOperationException(
          $"Plaid /transactions/get failed: {response.Error.ErrorCode}: {response.Error.ErrorMessage}");
      }

      // Keep the first page — it carries the account balances the sync path persists.
      firstPage ??= response;

      var page = response.Transactions;
      if (page != null && page.Count > 0)
      {
        allTransactions.AddRange(page);
      }

      // Stop once we've pulled everything Plaid reports, or when a page comes back empty
      // (defensive: guarantees termination even if the offset somehow can't advance).
      if (page == null
        || page.Count == 0
        || allTransactions.Count >= response.TotalTransactions)
      {
        break;
      }
    }

    return (firstPage, allTransactions);
  }

  public async Task<int> SyncRecentTransactionsAsync(string accessToken, DateTime fromDate, DateTime toDate)
  {
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    try
    {
      _logger.LogDebug("Syncing recent transactions from {FromDate} to {ToDate}", fromDate, toDate);

      var (firstPage, plaidTransactions) = await FetchAllTransactionsAsync(
        accessToken,
        DateOnly.FromDateTime(fromDate),
        DateOnly.FromDateTime(toDate),
        req => _plaidClient.TransactionsGetAsync(req));

      // Update account balances from the first page (TransactionsGet includes account data)
      if (firstPage?.Accounts != null && firstPage.Accounts.Any())
      {
        foreach (var plaidAccount in firstPage.Accounts)
        {
          var existingAccount = await _accountRepository.GetByPlaidAccountIdAsync(plaidAccount.AccountId);
          if (existingAccount != null)
          {
            existingAccount.CurrentBalance = (decimal)(plaidAccount.Balances.Current ?? 0);
            existingAccount.AvailableBalance = (decimal)(plaidAccount.Balances.Available ?? 0);
            existingAccount.UpdatedAt = DateTime.UtcNow;
            await _accountRepository.UpdateAsync(existingAccount);
            _logger.LogDebug("Updated balance for account {AccountId}: Current={Current}, Available={Available}",
              plaidAccount.AccountId, existingAccount.CurrentBalance, existingAccount.AvailableBalance);
          }
        }
      }

      if (plaidTransactions.Count == 0)
      {
        _logger.LogDebug("No transactions returned for sync period {FromDate} to {ToDate}", fromDate, toDate);
        return 0;
      }

      var ingest = await _transactionIngest.IngestAsync(
        plaidTransactions.Select(ToIncomingTransaction).ToList());
      var newTransactions = ingest.Created;

      if (newTransactions.Count > 0)
      {
        _logger.LogDebug("Synced {Count} new transactions for date range {FromDate} to {ToDate}",
          newTransactions.Count, fromDate, toDate);
      }

      // Record metrics for background sync
      stopwatch.Stop();
      _metrics.RecordPlaidSync(newTransactions.Count);
      _metrics.RecordPlaidSyncDuration(stopwatch.Elapsed.TotalSeconds);

      return newTransactions.Count;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error syncing recent transactions for date range {FromDate} to {ToDate}", fromDate, toDate);
      throw;
    }
  }

  /// <summary>
  /// Deletes transactions that Plaid reported as removed (TRANSACTIONS_REMOVED webhook, e.g. a
  /// cancelled pending charge or a reversed posted transaction). Without this, a removed row
  /// persists forever and keeps counting toward spend totals, budget usage, and AI insights.
  /// Rows the user has touched (categorized/annotated/budgeted/split/etc.) are preserved — the
  /// same guard <see cref="IsUntouchedByUser"/> that pending-hold cleanup uses — so no user work
  /// is destroyed. Returns the number of rows actually deleted.
  /// </summary>
  public async Task<int> RemoveTransactionsAsync(IEnumerable<string> plaidTransactionIds)
  {
    if (plaidTransactionIds == null)
    {
      return 0;
    }

    var removed = 0;
    foreach (var plaidId in plaidTransactionIds.Where(id => !string.IsNullOrEmpty(id)).Distinct())
    {
      var transaction = await _transactionRepository.GetByPlaidTransactionIdAsync(plaidId);
      if (transaction == null)
      {
        continue;
      }

      if (!IsUntouchedByUser(transaction))
      {
        _logger.LogInformation(
          "Plaid removed transaction {PlaidTransactionId} but the user has annotated it; keeping row {Id} for manual review",
          plaidId, transaction.Id);
        continue;
      }

      await _transactionRepository.DeleteAsync(transaction.Id);
      removed++;
    }

    if (removed > 0)
    {
      _logger.LogInformation("Removed {Count} transactions per Plaid TRANSACTIONS_REMOVED", removed);
    }

    return removed;
  }

  // Reconciles a pending authorization hold against its final posted charge so users
  // don't see both.
  //
  // The authoritative link is Plaid's pending_transaction_id, which the posted charge
  // carries back to its hold — handled INLINE during sync (merge-in-place, immediate, no
  // data loss). This pass is only a fallback for institutions that don't populate that
  // field. To stay safe it is deliberately conservative and only removes a hold that:
  //   - the user has NOT touched (no category/notes/tags/savings/budget/split) — so no
  //     user work is ever destroyed;
  //   - has AGED past the background re-fetch window (default 8 days > the 7-day window)
  //     — so a just-settled hold Plaid hasn't dropped yet can't be deleted-then-re-added
  //     in a churn loop (which would also re-cost AI categorization);
  //   - has a matching posted charge on the same account + merchant + sign, settled
  //     nearby, with a proportional amount tolerance (hold amounts routinely differ from
  //     the final charge, e.g. groceries $284 -> $254).
  // Linking institutions still dedupe instantly via the inline path; only the
  // non-linking fallback waits for the age gate.
  public async Task CleanupStalePendingTransactionsAsync(int userId, int staleDaysThreshold = 8)
  {
    try
    {
      // Wide enough window to pair a hold with its later posted charge.
      var allUserTransactions = (await _transactionRepository.GetByUserIdAsync(
        userId,
        DateTime.UtcNow.AddDays(-60),
        DateTime.UtcNow.AddDays(1))).ToList();

      // Age gate: only consider holds older than the background re-fetch window so a
      // deleted hold can't be re-added on the next sync.
      var ageCutoff = DateTime.UtcNow.AddDays(-Math.Max(1, staleDaysThreshold));
      var agedUntouchedPending = allUserTransactions
        .Where(t => t.IsPending && t.Date < ageCutoff && IsUntouchedByUser(t))
        .ToList();
      if (agedUntouchedPending.Count == 0)
      {
        _logger.LogDebug("No reconcilable pending transactions for user {UserId}", userId);
        return;
      }

      var postedTransactions = allUserTransactions.Where(t => !t.IsPending).ToList();
      var supersededIds = FindSupersededPendingIds(agedUntouchedPending, postedTransactions);

      if (supersededIds.Count > 0)
      {
        foreach (var id in supersededIds)
        {
          await _transactionRepository.DeleteAsync(id);
        }
        _logger.LogInformation(
          "Removed {Count} pending transactions superseded by posted charges for user {UserId}",
          supersededIds.Count, userId);
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error cleaning up stale pending transactions for user {UserId}", userId);
      throw;
    }
  }

  /// <summary>
  /// True when the user hasn't categorized, annotated, tagged, budgeted, linked to a
  /// savings goal, or split the transaction — i.e. it's safe to remove a duplicate of it
  /// without destroying any user work.
  /// </summary>
  internal static bool IsUntouchedByUser(Core.Entities.Transaction t) =>
    !t.IsUserCategorized
    && !t.IsSplit
    && t.ParentTransactionId == null
    && t.SavingsGoalId == null
    && t.BudgetId == null
    && t.PersonalUserCategoryId == null
    && string.IsNullOrEmpty(t.UserNotes)
    && string.IsNullOrEmpty(t.SemanticTags);

  /// <summary>
  /// Returns the ids of pending holds that have been superseded by a posted charge.
  /// A hold matches a posted charge on the same account + merchant + sign, settled within
  /// roughly two weeks, with a proportional amount tolerance (max of $5 or 25%) because a
  /// hold amount often differs from the final charge. Each posted charge supersedes at
  /// most one hold. Callers should pre-filter to aged, user-untouched holds. Pure and
  /// deterministic for unit testing.
  /// </summary>
  internal static List<int> FindSupersededPendingIds(
    IReadOnlyList<Core.Entities.Transaction> pending,
    IReadOnlyList<Core.Entities.Transaction> posted)
  {
    var usedPostedIds = new HashSet<int>();
    var superseded = new List<int>();

    foreach (var hold in pending.OrderBy(p => p.Date).ThenBy(p => p.Id))
    {
      var merchant = NormalizedMerchant(hold);
      if (string.IsNullOrEmpty(merchant))
      {
        continue; // Without a merchant anchor we can't match safely.
      }

      // Proportional tolerance with a small floor; large enough for grocery/tip/fuel
      // variance but not so large it merges unrelated small purchases.
      var amountTolerance = Math.Max(5m, 0.25m * Math.Abs(hold.Amount));

      var match = posted
        .Where(charge => !usedPostedIds.Contains(charge.Id)
          && charge.AccountId == hold.AccountId
          && NormalizedMerchant(charge) == merchant
          && Math.Sign(charge.Amount) == Math.Sign(hold.Amount) // never a refund/credit
          && (charge.Date - hold.Date).TotalDays >= -1
          && (charge.Date - hold.Date).TotalDays <= 14
          && Math.Abs(charge.Amount - hold.Amount) <= amountTolerance)
        .OrderBy(charge => Math.Abs((charge.Date - hold.Date).TotalDays))
        .ThenBy(charge => Math.Abs(charge.Amount - hold.Amount))
        .FirstOrDefault();

      if (match != null)
      {
        usedPostedIds.Add(match.Id); // One posted charge supersedes at most one hold.
        superseded.Add(hold.Id);
      }
    }

    return superseded;
  }

  private static string NormalizedMerchant(Core.Entities.Transaction t)
  {
    var name = !string.IsNullOrWhiteSpace(t.MerchantName) ? t.MerchantName : t.Description;
    return (name ?? string.Empty).Trim().ToLowerInvariant();
  }

  /// <summary>
  /// Promotes an existing pending hold row to its posted charge in place: swaps the Plaid id,
  /// clears the pending flag, and refreshes amount/date/merchant from the posted Plaid
  /// transaction. Pure/mutating so all three sync/import paths share one implementation and it
  /// can be unit-tested without the Plaid SDK. Amount follows the repo sign convention
  /// (expense negative): Plaid reports a debit as a positive number, so we negate it.
  /// </summary>
  internal static void PromotePendingToPosted(
    Core.Entities.Transaction pending,
    string postedPlaidTransactionId,
    decimal? plaidAmount,
    DateOnly? plaidDate,
    string? merchantName) =>
    TransactionIngestService.PromotePendingToPosted(
      pending, postedPlaidTransactionId, -(decimal)(plaidAmount ?? 0), plaidDate, merchantName);

  public async Task ImportHistoricalDataAsync(int userId, string itemId, HistoricalDataRange range, 
    DateTime? customStartDate = null, DateTime? customEndDate = null)
  {
    try
    {
      var connection = await _plaidConnectionRepository.GetByItemIdAsync(itemId);
      if (connection == null)
      {
        throw new ArgumentException($"Connection not found for item {itemId}");
      }

      var (startDate, endDate) = CalculateDateRange(range, customStartDate, customEndDate);
      
      _logger.LogInformation("Starting historical data import for user {UserId}, range: {Range}, dates: {StartDate} to {EndDate}", 
        userId, range, startDate, endDate);

      var accessToken = _encryptionService.Decrypt(connection.AccessToken);

      // Delegate to the date-honoring overload, which batches the range into 30-day
      // windows and (via FetchAllTransactionsAsync) pages through each window. The previous
      // implementation called SyncTransactionsForConnectionAsync, which re-derives its own
      // LastSyncAt..now window and so ignored the computed historical range entirely —
      // importing roughly a single day instead of the requested span.
      await ImportHistoricalDataAsync(accessToken, startDate, endDate, itemId);

      connection.LastSyncAt = DateTime.UtcNow;
      await _plaidConnectionRepository.UpdateAsync(connection);

      _logger.LogInformation("Completed historical data import for user {UserId}", userId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error importing historical data for user {UserId}, item {ItemId}", userId, itemId);
      throw;
    }
  }

  public async Task ImportHistoricalDataAsync(string accessToken, DateTime startDate, DateTime endDate,
    string? itemId = null)
  {
    try
    {
      _logger.LogInformation("Starting historical data import for date range: {StartDate} to {EndDate}",
        startDate, endDate);

      var batchSize = TimeSpan.FromDays(30);
      var currentDate = startDate;

      while (currentDate < endDate)
      {
        var batchEndDate = currentDate.Add(batchSize);
        if (batchEndDate > endDate) batchEndDate = endDate;

        // Pass itemId when we have one so each page call is rate-limited (429 backoff)
        // like the connection-sync path; without one, calls go direct (legacy behavior).
        var (_, plaidTransactions) = await FetchAllTransactionsAsync(
          accessToken,
          DateOnly.FromDateTime(currentDate),
          DateOnly.FromDateTime(batchEndDate),
          req => _plaidClient.TransactionsGetAsync(req),
          itemId);

        await ProcessTransactionsResponse(plaidTransactions);

        currentDate = batchEndDate;
        
        await Task.Delay(1000);
      }

      _logger.LogInformation("Completed historical data import for date range: {StartDate} to {EndDate}", 
        startDate, endDate);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error importing historical data for date range: {StartDate} to {EndDate}", 
        startDate, endDate);
      throw;
    }
  }

  /// <summary>
  /// Refreshes a stored account from what Plaid returned. An account that was deactivated because its
  /// bank was removed comes back active under the connection it was just linked on.
  /// </summary>
  internal static void ApplySyncedAccount(Core.Entities.Account existingAccount, Going.Plaid.Entity.Account plaidAccount, int? connectionId, int? userFamilyId)
  {
    existingAccount.Name = plaidAccount.Name;
    existingAccount.OfficialName = plaidAccount.OfficialName ?? plaidAccount.Name;
    existingAccount.Mask = plaidAccount.Mask;
    existingAccount.CurrentBalance = (decimal)(plaidAccount.Balances.Current ?? 0);
    existingAccount.AvailableBalance = (decimal)(plaidAccount.Balances.Available ?? 0);
    existingAccount.IsActive = true;
    if (connectionId != null) existingAccount.PlaidConnectionId = connectionId;
    // Backfill FamilyId if the user joined a family after the account was first synced
    if (existingAccount.FamilyId == null && userFamilyId != null)
      existingAccount.FamilyId = userFamilyId;
  }

  private async Task SyncAccountsAsync(int userId, string accessToken)
  {
    try
    {
      var connection = await _plaidConnectionRepository.GetByUserIdAsync(userId);
      var currentConnection = connection
        .Where(c => c.Provider == BankProvider.Plaid)
        .FirstOrDefault(c => _encryptionService.Decrypt(c.AccessToken) == accessToken);
      
      if (currentConnection?.ItemId == null)
      {
        _logger.LogWarning("No connection or ItemId found for user {UserId}", userId);
        return;
      }

      var request = new AccountsGetRequest { AccessToken = accessToken };
      
      var response = await ExecuteWithRateLimit(
        currentConnection.ItemId,
        () => _plaidClient.AccountsGetAsync(request),
        _plaidUsageTracker.CanMakeBalanceRequest,
        _plaidUsageTracker.RecordBalanceRequest,
        "AccountsGet"
      );

      var user = await _userRepository.GetByIdAsync(userId);

      // First-link currency default (#941 step 2): registration assigns "USD" without
      // asking, so the first bank account the user ever links is the best signal of
      // their real currency. Adopt it once, and only while the user hasn't explicitly
      // chosen (CurrencyIsUserSet) — a later foreign-currency account never flips an
      // established user.
      var mayAdoptCurrency = user is { CurrencyIsUserSet: false }
        && !(await _accountRepository.GetByUserIdAsync(userId)).Any();

      foreach (var plaidAccount in response.Accounts)
      {
        var accountType = MapAccountType(plaidAccount.Type);

        // #310: BuoyAI budgets with deposit accounts + credit cards only. Skip investment/brokerage
        // (e.g. Robinhood) and loans — never create a local Account for them, which also drops their
        // transactions (the sync links each txn to a local account and skips when none exists).
        if (!AccountTypePolicy.IsBudgetRelevant(accountType))
        {
          // If a previous sync (before this policy) already stored it, retire it rather than leave a
          // stale row — soft-deactivate so it stops surfacing, without destroying history.
          var stale = await _accountRepository.GetByPlaidAccountIdAsync(plaidAccount.AccountId);
          if (stale is { IsActive: true })
          {
            stale.IsActive = false;
            stale.UpdatedAt = DateTime.UtcNow;
            await _accountRepository.UpdateAsync(stale);
            _logger.LogInformation("Deactivated now-out-of-scope account {AccountId} (Plaid type {PlaidType}) for user {UserId} (#310)",
              plaidAccount.AccountId, plaidAccount.Type, userId);
          }
          else
          {
            _logger.LogInformation("Skipping out-of-scope account {AccountId} (Plaid type {PlaidType}) for user {UserId} — not budget-relevant (#310)",
              plaidAccount.AccountId, plaidAccount.Type, userId);
          }
          continue;
        }

        var existingAccount = await _accountRepository.GetByPlaidAccountIdAsync(plaidAccount.AccountId);

        if (existingAccount == null)
        {
          var account = new Core.Entities.Account
          {
            UserId = userId,
            FamilyId = user?.FamilyId,
            PlaidConnectionId = currentConnection?.Id,
            PlaidAccountId = plaidAccount.AccountId,
            Name = plaidAccount.Name,
            OfficialName = plaidAccount.OfficialName ?? plaidAccount.Name,
            Mask = plaidAccount.Mask,
            Type = accountType,
            Subtype = plaidAccount.Subtype?.ToString() ?? string.Empty,
            CurrentBalance = (decimal)(plaidAccount.Balances.Current ?? 0),
            AvailableBalance = (decimal)(plaidAccount.Balances.Available ?? 0),
            Currency = plaidAccount.Balances.IsoCurrencyCode ?? "USD",
            IsActive = true
          };

          await _accountRepository.CreateAsync(account);

          if (mayAdoptCurrency)
          {
            mayAdoptCurrency = false;
            var adopted = CurrencyCodes.Normalize(account.Currency);
            if (adopted != null && user!.Currency != adopted)
            {
              user.Currency = adopted;
              user.UpdatedAt = DateTime.UtcNow;
              await _userRepository.UpdateAsync(user);
              _logger.LogInformation(
                "Defaulted primary currency to {Currency} from first linked account for user {UserId} (#941)",
                adopted, userId);
            }
          }
        }
        else
        {
          ApplySyncedAccount(existingAccount, plaidAccount, currentConnection?.Id, user?.FamilyId);
          await _accountRepository.UpdateAsync(existingAccount);
        }
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error syncing accounts for user {UserId}", userId);
      throw;
    }
  }

  private async Task<TransactionSyncResult> SyncTransactionsForConnectionAsync(Core.Entities.PlaidConnection connection, string accessToken)
  {
    var result = new TransactionSyncResult();
    
    try
    {
      var endDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
      var startDate = DateOnly.FromDateTime(connection.LastSyncAt.Date);

      if (string.IsNullOrEmpty(connection.ItemId))
      {
        _logger.LogWarning("No ItemId found for connection {ConnectionId}", connection.Id);
        return result;
      }

      var itemId = connection.ItemId;
      var (_, plaidTransactions) = await FetchAllTransactionsAsync(
        accessToken,
        startDate,
        endDate,
        req => ExecuteWithRateLimit(
          itemId,
          () => _plaidClient.TransactionsGetAsync(req),
          _plaidUsageTracker.CanMakeRefreshRequest,
          _plaidUsageTracker.RecordRefreshRequest,
          "TransactionsGet"));

      if (plaidTransactions.Count == 0)
      {
        _logger.LogDebug("No transactions returned for connection {ConnectionId}", connection.Id);
        return result;
      }

      var ingest = await _transactionIngest.IngestAsync(
        plaidTransactions.Select(ToIncomingTransaction).ToList());

      if (ingest.MergedPendingCount > 0)
      {
        _logger.LogInformation("Updated {Count} pending transactions to posted status for connection {ItemId}",
          ingest.MergedPendingCount, connection.ItemId);
      }

      if (ingest.Created.Count > 0)
      {
        _logger.LogInformation("Synced {Count} new transactions for connection {ItemId}",
          ingest.Created.Count, connection.ItemId);

        result.TotalNewTransactions = ingest.Created.Count;
        result.TransactionsNeedingAI = ingest.NeedsReviewCount;
        result.RulesApplied = ingest.Categorization.RulesApplied;
        result.QueuedTransactionIds = ingest.Categorization.QueuedTransactionIds;
      }

      return result;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error syncing transactions for connection {ItemId}", connection.ItemId);
      throw;
    }
  }

  private async Task ProcessTransactionsResponse(IReadOnlyList<Going.Plaid.Entity.Transaction> transactions)
  {
    if (transactions == null || transactions.Count == 0)
    {
      _logger.LogDebug("No transactions in response to process.");
      return;
    }

    var ingest = await _transactionIngest.IngestAsync(
      transactions.Select(ToIncomingTransaction).ToList());

    if (ingest.MergedPendingCount > 0)
    {
      _logger.LogInformation("Updated {Count} pending transactions to posted status from historical import", ingest.MergedPendingCount);
    }

    if (ingest.Created.Count > 0)
    {
      _logger.LogInformation("Processed {Count} new transactions from historical import", ingest.Created.Count);
    }
  }

  private (DateTime startDate, DateTime endDate) CalculateDateRange(HistoricalDataRange range, 
    DateTime? customStartDate = null, DateTime? customEndDate = null)
  {
    var endDate = DateTime.UtcNow.Date;
    var maxStartDate = endDate.AddYears(-2);
    
    var calculatedRange = range switch
    {
      HistoricalDataRange.TwoMonths => (endDate.AddMonths(-2), endDate),
      HistoricalDataRange.OneYear => (endDate.AddYears(-1), endDate),
      HistoricalDataRange.TwoYears => (maxStartDate, endDate),
      HistoricalDataRange.Custom => (customStartDate ?? endDate.AddMonths(-2), 
                                    customEndDate ?? endDate),
      _ => (endDate.AddMonths(-2), endDate)
    };
    
    if (calculatedRange.Item1 < maxStartDate)
    {
      calculatedRange.Item1 = maxStartDate;
    }
    
    return calculatedRange;
  }
}
