using System.Collections.Concurrent;
using System.IO.Compression;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.DTOs.Category;
using BuoyAI.Core.DTOs.Import;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Services;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Two-step file import (preview, then commit) for bank files (CSV, OFX, QFX) and for Actual
/// Budget and YNAB exports. Parsing lives in <c>Services/Import</c>; this class stages the parse,
/// maps accounts and categories, and writes through <see cref="ITransactionIngestService"/> so
/// imported rows get the same dedupe and categorization as bank-synced ones.
/// </summary>
public class ImportService : IImportService
{
  public static readonly TimeSpan StagingLifetime = TimeSpan.FromMinutes(30);
  public static readonly TimeSpan UndoWindow = TimeSpan.FromHours(24);
  public const int MaxRows = 100_000;
  public const int MaxPreviewRows = 200;
  public const int PreviewsPerHour = 10;
  public const int ReparsesPerHour = 60;
  public const long MaxTotalBytes = 25L * 1024 * 1024;
  public const long MaxPlainFileBytes = 5L * 1024 * 1024;
  private const int CommitBatchSize = 500;

  private static readonly ConcurrentDictionary<string, byte> CommitsInFlight = new();

  private readonly BuoyAIDbContext _db;
  private readonly ITransactionIngestService _ingest;
  private readonly ICategoryService _categoryService;
  private readonly IBudgetService _budgetService;
  private readonly IFamilyAuthorizationService _familyAuth;
  private readonly IMemoryCache _cache;
  private readonly ILogger<ImportService> _logger;
  private readonly ImportPlanner _planner;

  public ImportService(
    BuoyAIDbContext db,
    ITransactionIngestService ingest,
    ICategoryService categoryService,
    IBudgetService budgetService,
    IFamilyAuthorizationService familyAuth,
    IMemoryCache cache,
    ILogger<ImportService> logger)
  {
    _db = db;
    _ingest = ingest;
    _categoryService = categoryService;
    _budgetService = budgetService;
    _familyAuth = familyAuth;
    _cache = cache;
    _logger = logger;
    _planner = new ImportPlanner(db);
  }

  private sealed class StagedImport
  {
    public int UserId { get; init; }
    public List<ImportFileInput> Files { get; init; } = new();
    public ImportPreviewRequest Request { get; set; } = new();
    public ParsedImport Parsed { get; set; } = null!;
    public List<ParsedImportAccount> Accounts { get; set; } = new();
    public Dictionary<int, RowPlan> Plans { get; set; } = new();
    public DateTime ExpiresAt { get; init; }
  }

  private sealed record UserScope(int UserId, int? FamilyId, string Currency);

  // ==================== Preview ====================

  public async Task<ImportPreviewResult> PreviewAsync(int userId, IReadOnlyList<ImportFileInput> files, ImportPreviewRequest request)
  {
    EnforceRate(userId, "preview", PreviewsPerHour);

    if (files.Count == 0 || files.All(f => f.Content.Length == 0))
      throw new ImportException("No file was provided.");
    if (files.Sum(f => (long)f.Content.Length) > MaxTotalBytes)
      throw new ImportException("That file is too large. The limit is 25 MB.");

    var staged = new StagedImport
    {
      UserId = userId,
      Files = files.ToList(),
      Request = request,
      ExpiresAt = DateTime.UtcNow.Add(StagingLifetime)
    };
    return await BuildPreviewAsync(staged, Guid.NewGuid().ToString("N"));
  }

  public async Task<ImportPreviewResult> ReparseAsync(int userId, string importId, ImportPreviewRequest request)
  {
    EnforceRate(userId, "reparse", ReparsesPerHour);
    var old = GetStaged(userId, importId);
    var staged = new StagedImport
    {
      UserId = userId,
      Files = old.Files,
      Request = request,
      ExpiresAt = DateTime.UtcNow.Add(StagingLifetime)
    };
    return await BuildPreviewAsync(staged, importId);
  }

  private async Task<ImportPreviewResult> BuildPreviewAsync(StagedImport staged, string importId)
  {
    var scope = await GetScopeAsync(staged.UserId);
    var request = staged.Request;

    // Single-account files can be pointed at an existing account; check it before reading anything.
    Account? target = null;
    if (request.AccountId.HasValue)
    {
      target = await GetWritableAccountAsync(scope, request.AccountId.Value);
    }

    var parsed = ParseFiles(staged.Files, request.Source, request.Mapping);
    if (parsed.Rows.Count > MaxRows)
      throw new ImportException($"That file has more than {MaxRows:N0} transactions. Split it and import the parts one at a time.");
    AddBudgetOnlyCategories(parsed);
    staged.Parsed = parsed;

    // Accounts: one entry per account the file names (or a single stand-in for plain files).
    staged.Accounts = BuildAccountList(parsed, staged.Files);
    var previews = await ProposeAccountsAsync(scope, staged.Accounts, parsed, target);

    // Classify rows against stored data, per matched account.
    var plans = new Dictionary<int, RowPlan>();
    foreach (var group in parsed.Rows.GroupBy(r => AccountKeyFor(r, staged.Accounts, target)))
    {
      var preview = previews.FirstOrDefault(p => p.SourceKey == group.Key);
      if (preview?.MatchedAccountId != null)
      {
        foreach (var kv in await _planner.ClassifyAsync(parsed.Source, preview.MatchedAccountId.Value, group.ToList()))
          plans[kv.Key] = kv.Value;
      }
      else
      {
        foreach (var row in group)
          plans[row.RowId] = row.Error != null || !row.Date.HasValue
            ? new RowPlan(ImportRowClass.Invalid, null, row.Error ?? "The date could not be read.")
            : new RowPlan(ImportRowClass.New, null, null);
      }
    }
    staged.Plans = plans;

    foreach (var account in previews)
    {
      foreach (var row in parsed.Rows.Where(r => AccountKeyFor(r, staged.Accounts, target) == account.SourceKey))
      {
        if (!plans.TryGetValue(row.RowId, out var plan)) continue;
        if (plan.Class == ImportRowClass.New) account.NewCount++;
        else if (plan.Class == ImportRowClass.DuplicateLikely) account.DuplicateLikelyCount++;
        else if (plan.Class == ImportRowClass.PossibleDuplicate) account.PossibleDuplicateCount++;
      }
    }

    var categories = await ProposeCategoriesAsync(scope, parsed);

    _cache.Set(CacheKey(staged.UserId, importId), staged, staged.ExpiresAt);

    var result = new ImportPreviewResult
    {
      ImportId = importId,
      ExpiresAt = staged.ExpiresAt,
      Detected = parsed.Detected,
      Accounts = previews,
      Categories = categories,
      Summary = Summarize(parsed, plans),
      Rows = parsed.Rows.Take(MaxPreviewRows).Select(r => ToRowPreview(r, plans)).ToList(),
      Warnings = parsed.Warnings.ToList(),
      NeedsDateOrderChoice = parsed.DateOrderAmbiguous && request.Mapping.DateOrder == ImportDateOrder.Auto,
      Budget = SummarizeBudget(parsed)
    };
    result.Detected.Source = parsed.Source.ToString().ToLowerInvariant();
    return result;
  }

  public Task<ImportRowsPage> GetRowsAsync(int userId, string importId, string? rowClass, int page, int pageSize)
  {
    var staged = GetStaged(userId, importId);
    pageSize = Math.Clamp(pageSize, 1, 500);
    page = Math.Max(page, 0);
    var rows = staged.Parsed.Rows.AsEnumerable();
    if (!string.IsNullOrWhiteSpace(rowClass))
      rows = rows.Where(r => staged.Plans.TryGetValue(r.RowId, out var p) && p.Class == rowClass);
    var all = rows.ToList();
    return Task.FromResult(new ImportRowsPage
    {
      Page = page,
      PageSize = pageSize,
      Total = all.Count,
      Rows = all.Skip(page * pageSize).Take(pageSize).Select(r => ToRowPreview(r, staged.Plans)).ToList()
    });
  }

  // ==================== Commit ====================

  public async Task<ImportCommitResult> CommitAsync(int userId, string importId, ImportCommitRequest request)
  {
    var key = CacheKey(userId, importId);
    if (!CommitsInFlight.TryAdd(key, 0))
      throw new ImportException("This import is already running.", 409);
    try
    {
      // One transaction for the whole commit: accounts, categories, rows and budget land together
      // or not at all, so a failure partway never leaves partial data behind a retryable error.
      var strategy = _db.Database.CreateExecutionStrategy();
      var outcome = await strategy.ExecuteAsync(async () =>
      {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
          var committed = await CommitCoreAsync(userId, importId, request);
          await tx.CommitAsync();
          return committed;
        }
        catch
        {
          await tx.RollbackAsync();
          _db.ChangeTracker.Clear();
          throw;
        }
      });
      // Only once the transaction is durable: a failed or replayed commit still needs the preview.
      _cache.Remove(key);
      return outcome;
    }
    finally
    {
      CommitsInFlight.TryRemove(key, out _);
    }
  }

  private async Task<ImportCommitResult> CommitCoreAsync(int userId, string importId, ImportCommitRequest request)
  {
    var staged = GetStaged(userId, importId);
    var parsed = staged.Parsed;
    var scope = await GetScopeAsync(userId);

    if (parsed.DateOrderAmbiguous && staged.Request.Mapping.DateOrder == ImportDateOrder.Auto)
      throw new ImportException("Dates in this file can be read two ways. Choose day-first or month-first, then import.");

    Account? singleTarget = staged.Request.AccountId.HasValue
      ? await GetWritableAccountAsync(scope, staged.Request.AccountId.Value)
      : null;

    var batchId = Guid.NewGuid().ToString("N");
    var result = new ImportCommitResult { BatchId = batchId, UndoableUntil = DateTime.UtcNow.Add(UndoWindow) };
    result.Warnings.AddRange(parsed.Warnings);

    // ---- Accounts: map or create ----
    var proposals = await ProposeAccountsAsync(scope, staged.Accounts, parsed, singleTarget);
    var accountIdByKey = new Dictionary<string, int>();
    var currencyByAccount = new Dictionary<int, string>();
    var createdAccounts = new List<Account>();
    var createdSeq = 0;
    foreach (var proposal in proposals)
    {
      var choice = request.AccountMappings.FirstOrDefault(m => m.SourceKey == proposal.SourceKey);
      var action = choice?.Action?.ToLowerInvariant() ?? proposal.ProposedAction;
      if (action == "skip") continue;

      if (action == "match")
      {
        var accountId = choice?.AccountId ?? proposal.MatchedAccountId
          ?? throw new ImportException($"Choose an account for \"{proposal.SourceName}\".");
        var account = await GetWritableAccountAsync(scope, accountId);
        accountIdByKey[proposal.SourceKey] = account.Id;
        currencyByAccount[account.Id] = account.Currency;
        continue;
      }

      if (scope.FamilyId != null && !await _familyAuth.CanModifyFinancialDataAsync(scope.UserId))
        throw new ImportException("Your family role is read-only, so you cannot import here.", 403);

      var name = SafeName(choice?.Name, proposal.SourceName);
      var type = ParseAccountType(choice?.Type ?? proposal.ProposedType);
      var created = new Account
      {
        UserId = userId,
        FamilyId = scope.FamilyId,
        PlaidConnectionId = null,
        PlaidAccountId = $"import-{batchId}-{createdSeq++}",
        Name = name,
        OfficialName = name,
        Type = type,
        Subtype = "imported",
        Currency = scope.Currency,
        IsActive = !proposal.Closed,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
      };
      _db.Accounts.Add(created);
      createdAccounts.Add(created);
      await _db.SaveChangesAsync();
      accountIdByKey[proposal.SourceKey] = created.Id;
      currencyByAccount[created.Id] = created.Currency;
      result.AccountsCreated++;
    }
    result.AccountIds.AddRange(accountIdByKey.Values.Distinct());
    if (accountIdByKey.Count == 0)
      throw new ImportException("No accounts were chosen, so there is nothing to import.");

    // ---- Categories: map or create ----
    var categoryIdBySource = await ResolveCategoriesAsync(scope, parsed, request, result);

    // ---- Classify again against the accounts as they are now ----
    var plans = new Dictionary<int, RowPlan>();
    foreach (var group in parsed.Rows.GroupBy(r => AccountKeyFor(r, staged.Accounts, singleTarget)))
    {
      if (!accountIdByKey.TryGetValue(group.Key, out var accountId)) continue;
      var isNewAccount = createdAccounts.Any(a => a.Id == accountId);
      if (isNewAccount)
      {
        foreach (var row in group)
          plans[row.RowId] = row.Error != null || !row.Date.HasValue
            ? new RowPlan(ImportRowClass.Invalid, null, row.Error)
            : new RowPlan(ImportRowClass.New, null, null);
      }
      else
      {
        foreach (var kv in await _planner.ClassifyAsync(parsed.Source, accountId, group.ToList()))
          plans[kv.Key] = kv.Value;
      }
    }

    var include = (request.IncludeClasses ?? new List<string> { ImportRowClass.New, ImportRowClass.PossibleDuplicate }).ToHashSet();
    var toImport = new List<(ParsedImportRow Row, int AccountId)>();
    foreach (var row in parsed.Rows)
    {
      if (!plans.TryGetValue(row.RowId, out var plan)) continue; // account skipped
      switch (plan.Class)
      {
        case ImportRowClass.Invalid:
          result.SkippedInvalid++;
          continue;
        case ImportRowClass.DuplicateExact:
          result.SkippedDuplicates++;
          continue;
      }

      var wants = include.Contains(plan.Class);
      if (request.Overrides.TryGetValue(row.RowId, out var choice))
      {
        if (choice.Equals("skip", StringComparison.OrdinalIgnoreCase)) wants = false;
        else if (choice.Equals("import", StringComparison.OrdinalIgnoreCase)) wants = true;
      }
      if (!wants)
      {
        if (plan.Class is ImportRowClass.DuplicateLikely or ImportRowClass.PossibleDuplicate) result.SkippedDuplicates++;
        else result.SkippedByChoice++;
        continue;
      }
      toImport.Add((row, accountIdByKey[AccountKeyFor(row, staged.Accounts, singleTarget)]));
    }

    // ---- Write through the shared ingest path ----
    var accountsById = await _db.Accounts.AsNoTracking()
      .Where(a => accountIdByKey.Values.Contains(a.Id))
      .ToDictionaryAsync(a => a.Id);

    foreach (var batch in toImport.Chunk(CommitBatchSize))
    {
      var incoming = batch.Select(item => ToIncoming(parsed.Source, item.Row, accountsById[item.AccountId], categoryIdBySource, batchId)).ToList();
      var outcome = await _ingest.IngestAsync(incoming);
      result.Imported += outcome.Created.Count;
      result.SkippedDuplicates += incoming.Count - outcome.Created.Count;
      result.RulesApplied += outcome.Categorization.RulesApplied;
      result.QueuedForAi += outcome.Categorization.QueuedForAi;
    }

    // An imported account has no bank balance to read, so for full histories (Actual, YNAB, which
    // include starting balances) derive it from the rows just written. Bank-file imports leave it alone.
    if (parsed.Source is ImportSource.Actual or ImportSource.Ynab)
    {
      foreach (var account in createdAccounts)
      {
        var sum = toImport.Where(i => i.AccountId == account.Id).Sum(i => i.Row.Amount);
        var tracked = await _db.Accounts.FirstAsync(a => a.Id == account.Id);
        tracked.CurrentBalance = tracked.Type == AccountType.CreditCard ? -sum : sum;
        tracked.AvailableBalance = tracked.CurrentBalance;
        tracked.UpdatedAt = DateTime.UtcNow;
      }
      await _db.SaveChangesAsync();
    }

    // ---- Budget ----
    if (request.CreateBudget)
      await CreateBudgetAsync(userId, parsed, categoryIdBySource, result);

    _logger.LogInformation(
      "User {UserId} imported {Imported} transactions from {Source} (batch {Batch}): {Dupes} duplicates, {Invalid} invalid, {Accounts} accounts and {Categories} categories created",
      userId, result.Imported, parsed.Source, batchId, result.SkippedDuplicates, result.SkippedInvalid, result.AccountsCreated, result.CategoriesCreated);
    return result;
  }

  private static IncomingTransaction ToIncoming(
    ImportSource source,
    ParsedImportRow row,
    Account account,
    Dictionary<string, int?> categoryIdBySource,
    string batchId)
  {
    int? categoryId = null;
    if (row.IsTransfer)
      categoryId = SystemCategoryIds.Transfer;
    else if (row.CategoryName != null && categoryIdBySource.TryGetValue(CategoryKey(row.CategoryGroup, row.CategoryName), out var mapped))
      categoryId = mapped;

    var userChose = categoryId.HasValue;
    var payee = SafeText(row.Payee, 200);
    return new IncomingTransaction
    {
      ExternalTransactionId = ImportPlanner.BuildExternalId(source, account.Id, row),
      ExternalAccountId = account.PlaidAccountId,
      Amount = row.Amount,
      Direction = row.Amount >= 0 ? TransactionType.Credit : TransactionType.Debit,
      Date = row.Date,
      Description = SafeText(row.Payee, 500),
      MerchantName = payee,
      OriginalDescription = row.OriginalDescription != null ? SafeText(row.OriginalDescription, 500) : null,
      IsoCurrencyCode = account.Currency,
      CheckNumber = row.CheckNumber,
      CategoryId = categoryId ?? SystemCategoryIds.Uncategorized,
      CategorySource = "Import",
      IsUserCategorized = userChose,
      NeedsReview = !userChose,
      UserNotes = row.Memo,
      ImportBatchId = batchId
    };
  }

  // ==================== Undo ====================

  public async Task<ImportUndoResult> UndoAsync(int userId, string batchId)
  {
    var scope = await GetScopeAsync(userId);
    var rows = await _db.Transactions
      .Include(t => t.Account)
      .Where(t => t.ImportBatchId == batchId)
      .ToListAsync();
    var imported = await _db.Accounts.Where(a => a.PlaidAccountId.StartsWith("import-" + batchId)).ToListAsync();

    if (rows.Count == 0 && imported.Count == 0)
      throw new ImportException("That import was not found.", 404);

    // Every affected account must be one this user may change.
    foreach (var accountId in rows.Select(r => r.AccountId).Concat(imported.Select(a => a.Id)).Distinct())
      await GetWritableAccountAsync(scope, accountId);

    var started = rows.Count > 0 ? rows.Min(r => r.CreatedAt) : imported.Min(a => a.CreatedAt);
    if (DateTime.UtcNow - started > UndoWindow)
      throw new ImportException("This import is more than a day old, so it can no longer be undone here.");

    var result = new ImportUndoResult { TransactionsRemoved = rows.Count };
    _db.Transactions.RemoveRange(rows);
    await _db.SaveChangesAsync();

    foreach (var account in imported)
    {
      var stillHasRows = await _db.Transactions.AnyAsync(t => t.AccountId == account.Id);
      if (stillHasRows) continue;
      _db.Accounts.Remove(account);
      result.AccountsRemoved++;
    }
    await _db.SaveChangesAsync();

    _logger.LogInformation("User {UserId} undid import batch {Batch}: {Tx} transactions, {Accounts} accounts", userId, batchId, result.TransactionsRemoved, result.AccountsRemoved);
    return result;
  }

  // ==================== Parsing dispatch ====================

  private static ParsedImport ParseFiles(IReadOnlyList<ImportFileInput> files, ImportSource requested, ImportMapping mapping)
  {
    foreach (var file in files)
    {
      var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
      if (ext is not (".csv" or ".tsv" or ".txt" or ".ofx" or ".qfx" or ".zip"))
        throw new ImportException($"\"{file.FileName}\" is not a supported file type. Use CSV, OFX, QFX, or a zip from Actual Budget or YNAB.");
      if (ext != ".zip" && file.Content.Length > MaxPlainFileBytes)
        throw new ImportException("That file is too large. Bank files can be up to 5 MB.");
    }

    // A zip is either an Actual Budget export or a YNAB export (its CSV files zipped).
    var zip = files.FirstOrDefault(f => ActualImportParser.IsZip(f.Content));
    if (zip != null)
    {
      if (requested is ImportSource.Auto or ImportSource.Actual && ActualImportParser.LooksLikeActualZip(zip.Content))
        return ActualImportParser.Parse(zip.Content);

      var csvs = ReadZippedCsvs(zip.Content);
      if (csvs.Count == 0)
        throw new ImportException("That zip has neither an Actual Budget database nor YNAB CSV files.");
      return YnabImportParser.Parse(csvs, mapping);
    }

    if (requested == ImportSource.Actual)
      throw new ImportException("Choose the .zip you get from Actual Budget (Settings, Export data).");

    if (requested is ImportSource.Auto or ImportSource.Ynab)
    {
      var ynabFiles = files.Where(f => YnabImportParser.Sniff(f.Content) != YnabImportParser.YnabFileKind.None).ToList();
      if (ynabFiles.Count > 0)
        return YnabImportParser.Parse(ynabFiles.Select(f => f.Content).ToList(), mapping);
      if (requested == ImportSource.Ynab)
        throw new ImportException("That does not look like a YNAB export. Use the Register and Plan files from Export Plan.");
    }

    var first = files[0];
    var isOfx = requested == ImportSource.Ofx
                || (requested == ImportSource.Auto
                    && (first.FileName.EndsWith(".ofx", StringComparison.OrdinalIgnoreCase)
                        || first.FileName.EndsWith(".qfx", StringComparison.OrdinalIgnoreCase)
                        || OfxImportParser.LooksLikeOfx(first.Content)));
    return isOfx ? OfxImportParser.Parse(first.Content, mapping) : CsvImportParser.Parse(first.Content, mapping);
  }

  private static List<byte[]> ReadZippedCsvs(byte[] zipBytes)
  {
    var result = new List<byte[]>();
    ZipArchive archive;
    try
    {
      archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
    }
    catch (InvalidDataException)
    {
      throw new ImportException("That file is not a valid zip.");
    }

    using (archive)
    {
      long total = 0;
      foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                                                        || e.FullName.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase)).Take(20))
      {
        using var input = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
          total += read;
          if (total > MaxTotalBytes) throw new ImportException("The files inside that zip are too large to import.");
          buffer.Write(chunk, 0, read);
        }
        result.Add(buffer.ToArray());
      }
    }
    return result;
  }

  // ==================== Accounts ====================

  private static List<ParsedImportAccount> BuildAccountList(ParsedImport parsed, IReadOnlyList<ImportFileInput> files)
  {
    var accounts = parsed.Accounts.ToList();
    foreach (var key in parsed.Rows.Select(r => r.AccountKey).Distinct())
    {
      if (accounts.Any(a => a.Key == key)) continue;
      var fallback = Path.GetFileNameWithoutExtension(files[0].FileName);
      accounts.Add(new ParsedImportAccount
      {
        Key = key,
        Name = key.Length > 0 ? key : (string.IsNullOrWhiteSpace(fallback) ? "Imported account" : fallback),
        Type = ImportAccountTypes.GuessFromName(key.Length > 0 ? key : fallback)
      });
    }
    if (accounts.Count == 0)
      accounts.Add(new ParsedImportAccount { Key = string.Empty, Name = Path.GetFileNameWithoutExtension(files[0].FileName) is { Length: > 0 } n ? n : "Imported account" });
    return accounts;
  }

  /// <summary>The account key a row belongs to. With a chosen target account every row goes to it.</summary>
  private static string AccountKeyFor(ParsedImportRow row, List<ParsedImportAccount> accounts, Account? singleTarget) =>
    singleTarget != null ? SingleTargetKey : row.AccountKey;

  private const string SingleTargetKey = "__target__";

  private async Task<List<ImportAccountPreview>> ProposeAccountsAsync(
    UserScope scope,
    List<ParsedImportAccount> sourceAccounts,
    ParsedImport parsed,
    Account? singleTarget)
  {
    if (singleTarget != null)
    {
      return new List<ImportAccountPreview>
      {
        new()
        {
          SourceKey = SingleTargetKey,
          SourceName = singleTarget.DisplayName,
          ProposedAction = "match",
          MatchedAccountId = singleTarget.Id,
          ProposedType = singleTarget.Type.ToString(),
          RowCount = parsed.Rows.Count
        }
      };
    }

    var existing = await _db.Accounts.AsNoTracking()
      .Where(a => a.UserId == scope.UserId || (scope.FamilyId != null && a.FamilyId == scope.FamilyId))
      .ToListAsync();
    var canWriteFamily = await _familyAuth.CanModifyFinancialDataAsync(scope.UserId);

    var previews = new List<ImportAccountPreview>();
    foreach (var source in sourceAccounts)
    {
      var match = existing.FirstOrDefault(a =>
        (a.UserId == scope.UserId || canWriteFamily)
        && (a.DisplayName.Equals(source.Name, StringComparison.OrdinalIgnoreCase)
            || a.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase)));
      previews.Add(new ImportAccountPreview
      {
        SourceKey = source.Key,
        SourceName = source.Name,
        ProposedAction = match != null ? "match" : "create",
        MatchedAccountId = match?.Id,
        ProposedType = match?.Type.ToString() ?? source.Type,
        Closed = source.Closed,
        RowCount = parsed.Rows.Count(r => r.AccountKey == source.Key)
      });
    }
    return previews;
  }

  private async Task<UserScope> GetScopeAsync(int userId)
  {
    var user = await _db.Users.AsNoTracking()
      .Where(u => u.Id == userId)
      .Select(u => new { u.FamilyId, u.Currency })
      .FirstOrDefaultAsync() ?? throw new ImportException("User not found.", 404);
    return new UserScope(userId, user.FamilyId, string.IsNullOrWhiteSpace(user.Currency) ? "USD" : user.Currency);
  }

  /// <summary>
  /// An account the user may add transactions to: their own, or a family account when their
  /// family role is not read-only. Anything else reads as not found, so ids cannot be probed.
  /// </summary>
  private async Task<Account> GetWritableAccountAsync(UserScope scope, int accountId)
  {
    var account = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId);
    var missing = new ImportException("Account not found or you do not have permission to import into it.", 404);
    if (account == null) throw missing;

    // A family account the user owns still follows their family role (a read-only member cannot
    // import into it). Personal accounts, or ones left over from a family they no longer belong to,
    // skip the family check.
    if (account.UserId == scope.UserId && (account.FamilyId == null || account.FamilyId != scope.FamilyId)) return account;
    if (scope.FamilyId != null && account.FamilyId == scope.FamilyId)
    {
      if (await _familyAuth.CanModifyFinancialDataAsync(scope.UserId)) return account;
      throw new ImportException("Your family role is read-only, so you cannot import here.", 403);
    }
    throw missing;
  }

  private static AccountType ParseAccountType(string? type) =>
    Enum.TryParse<AccountType>(type, true, out var parsed) ? parsed : AccountType.Checking;

  // ==================== Categories ====================

  private static string CategoryKey(string? group, string name) => $"{group?.Trim().ToLowerInvariant()}|{name.Trim().ToLowerInvariant()}";

  private async Task<List<ImportCategoryPreview>> ProposeCategoriesAsync(UserScope scope, ParsedImport parsed)
  {
    var known = await GetKnownCategoriesAsync(scope.UserId);
    var previews = new List<ImportCategoryPreview>();
    foreach (var cat in parsed.Categories)
    {
      var match = MatchCategory(known, cat);
      previews.Add(new ImportCategoryPreview
      {
        SourceGroup = cat.Group,
        SourceName = cat.Name,
        IsIncome = cat.IsIncome,
        ProposedCategoryId = match?.Id,
        ProposedCategoryName = match?.Name,
        ProposedNewName = cat.Name.Trim(),
        RowCount = parsed.Rows.Count(r => r.CategoryName != null
                                          && r.CategoryName.Equals(cat.Name, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(r.CategoryGroup ?? string.Empty, cat.Group, StringComparison.OrdinalIgnoreCase))
      });
    }
    return previews;
  }

  private async Task<List<CategoryDto>> GetKnownCategoriesAsync(int userId)
  {
    var all = await _categoryService.GetAllCategoriesAsync(userId);
    return all.SystemCategories.Concat(all.PersonalCategories).Concat(all.FamilyCategories).ToList();
  }

  private static CategoryDto? MatchCategory(List<CategoryDto> known, ParsedImportCategory cat)
  {
    // YNAB's pool of unassigned income has no BuoyAI twin; Other Income is the honest home.
    if (cat.Name.Equals("Ready to Assign", StringComparison.OrdinalIgnoreCase))
      return known.FirstOrDefault(k => k.Id == SystemCategoryIds.OtherIncome);

    var sameName = known.Where(k => k.Name.Equals(cat.Name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
    return sameName.FirstOrDefault(k => k.IsIncome == cat.IsIncome)
           ?? sameName.FirstOrDefault(k => !k.IsSystem)
           ?? sameName.FirstOrDefault();
  }

  private async Task<Dictionary<string, int?>> ResolveCategoriesAsync(
    UserScope scope,
    ParsedImport parsed,
    ImportCommitRequest request,
    ImportCommitResult result)
  {
    var known = await GetKnownCategoriesAsync(scope.UserId);
    var resolved = new Dictionary<string, int?>();

    foreach (var cat in parsed.Categories)
    {
      var key = CategoryKey(cat.Group, cat.Name);
      var choice = request.CategoryMappings.FirstOrDefault(m =>
        m.SourceName.Equals(cat.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(m.SourceGroup ?? string.Empty, cat.Group ?? string.Empty, StringComparison.OrdinalIgnoreCase));

      var proposed = MatchCategory(known, cat);
      var action = choice?.Action?.ToLowerInvariant() ?? (proposed != null ? "map" : "create");

      switch (action)
      {
        case "none":
          resolved[key] = null;
          break;

        case "map":
        {
          var id = choice?.CategoryId ?? proposed?.Id;
          if (id == null)
          {
            resolved[key] = null;
            break;
          }
          if (!await _categoryService.ValidateCategoryAccessAsync(id.Value, scope.UserId))
            throw new ImportException($"You cannot use that category for \"{cat.Name}\".", 403);
          resolved[key] = id;
          break;
        }

        default:
        {
          var name = SafeName(choice?.Name, cat.Name);
          var existing = known.FirstOrDefault(k => !k.IsSystem && k.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
          if (existing != null)
          {
            resolved[key] = existing.Id;
            break;
          }
          var created = await _categoryService.CreateCategoryAsync(
            new CreateCategoryDto { Name = name, IsIncome = cat.IsIncome }, scope.UserId);
          known.Add(created);
          resolved[key] = created.Id;
          result.CategoriesCreated++;
          break;
        }
      }
    }
    return resolved;
  }

  // ==================== Budget ====================

  /// <summary>A budgeted category may have no transactions in the file; it still needs a mapping to be budgeted.</summary>
  private static void AddBudgetOnlyCategories(ParsedImport parsed)
  {
    foreach (var entry in parsed.Budgets.Where(b => b.Budgeted != 0))
    {
      var known = parsed.Categories.Any(c =>
        c.Name.Equals(entry.Category, StringComparison.OrdinalIgnoreCase)
        && string.Equals(c.Group ?? string.Empty, entry.Group ?? string.Empty, StringComparison.OrdinalIgnoreCase));
      if (!known)
        parsed.Categories.Add(new ParsedImportCategory { Group = entry.Group ?? string.Empty, Name = entry.Category, IsIncome = false });
    }
  }

  private static ImportBudgetPreview? SummarizeBudget(ParsedImport parsed)
  {
    if (parsed.Budgets.Count == 0) return null;
    var months = parsed.Budgets.Select(b => b.Month).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
    var latest = months[^1];
    var latestEntries = parsed.Budgets.Where(b => b.Month == latest && b.Budgeted != 0).ToList();
    return new ImportBudgetPreview
    {
      Months = months,
      LatestMonth = latest,
      CategoriesWithAmounts = latestEntries.Count,
      LatestMonthTotal = latestEntries.Sum(b => b.Budgeted)
    };
  }

  private async Task CreateBudgetAsync(
    int userId,
    ParsedImport parsed,
    Dictionary<string, int?> categoryIdBySource,
    ImportCommitResult result)
  {
    var summary = SummarizeBudget(parsed);
    if (summary == null)
    {
      result.Warnings.Add("The file has no budget amounts, so no budget was created.");
      return;
    }

    // Creating a budget switches off the user's current one of the same type. An import must
    // never do that, so only fill the empty case.
    var hasActive = await _db.Budgets.AnyAsync(b => b.UserId == userId && b.IsActive && b.Type == BudgetType.Personal);
    if (hasActive)
    {
      result.Warnings.Add("You already have an active budget, so the imported budget amounts were not applied. Your existing budget is unchanged.");
      return;
    }

    var incomeNames = parsed.Categories.Where(c => c.IsIncome)
      .Select(c => CategoryKey(c.Group, c.Name)).ToHashSet();
    var lines = new Dictionary<int, CreateBudgetCategoryRequest>();
    foreach (var entry in parsed.Budgets.Where(b => b.Month == summary.LatestMonth && b.Budgeted > 0))
    {
      if (!categoryIdBySource.TryGetValue(CategoryKey(entry.Group, entry.Category), out var catId) || catId == null)
        continue;
      if (lines.TryGetValue(catId.Value, out var line))
      {
        line.AllocatedAmount += entry.Budgeted;
        continue;
      }
      lines[catId.Value] = new CreateBudgetCategoryRequest
      {
        CategoryId = catId.Value,
        AllocatedAmount = entry.Budgeted,
        IsIncome = incomeNames.Contains(CategoryKey(entry.Group, entry.Category)),
        AllowRollover = true
      };
    }
    if (lines.Count == 0)
    {
      result.Warnings.Add("None of the budgeted categories were mapped to Buoy Money categories, so no budget was created.");
      return;
    }

    var budget = await _budgetService.CreateBudgetAsync(userId, new CreateBudgetRequest
    {
      Name = $"Imported budget ({summary.LatestMonth})",
      Period = BudgetPeriod.Monthly,
      AutoReset = true,
      TotalIncome = lines.Values.Where(l => l.IsIncome).Sum(l => l.AllocatedAmount),
      Type = BudgetType.Personal,
      Categories = lines.Values.ToList()
    });
    result.BudgetCreated = true;
    result.BudgetId = budget.Id;
    result.Warnings.Add("Budget amounts come from the latest month in the file. Goals, targets and count-up are not imported.");
  }

  // ==================== Preview helpers ====================

  private static ImportSummary Summarize(ParsedImport parsed, Dictionary<int, RowPlan> plans)
  {
    var summary = new ImportSummary { Total = parsed.Rows.Count };
    foreach (var row in parsed.Rows)
    {
      var cls = plans.TryGetValue(row.RowId, out var plan) ? plan.Class : ImportRowClass.New;
      switch (cls)
      {
        case ImportRowClass.New: summary.New++; break;
        case ImportRowClass.DuplicateExact: summary.DuplicateExact++; break;
        case ImportRowClass.DuplicateLikely: summary.DuplicateLikely++; break;
        case ImportRowClass.PossibleDuplicate: summary.PossibleDuplicate++; break;
        case ImportRowClass.Invalid: summary.Invalid++; break;
      }
      if (row.Error != null || !row.Date.HasValue) continue;
      if (summary.FirstDate == null || row.Date < summary.FirstDate) summary.FirstDate = row.Date;
      if (summary.LastDate == null || row.Date > summary.LastDate) summary.LastDate = row.Date;
      if (row.Amount >= 0) summary.TotalInflow += row.Amount; else summary.TotalOutflow += -row.Amount;
    }
    return summary;
  }

  private static ImportRowPreview ToRowPreview(ParsedImportRow row, Dictionary<int, RowPlan> plans)
  {
    plans.TryGetValue(row.RowId, out var plan);
    return new ImportRowPreview
    {
      RowId = row.RowId,
      AccountKey = row.AccountKey,
      Date = row.Date,
      Amount = row.Amount,
      Payee = row.Payee,
      Memo = row.Memo,
      Category = row.CategoryName,
      Class = plan?.Class ?? ImportRowClass.New,
      MatchedTransactionId = plan?.MatchedTransactionId,
      Reason = plan?.Reason
    };
  }

  // ==================== Staging and limits ====================

  private static string CacheKey(int userId, string importId) => $"import:stage:{userId}:{importId}";

  private StagedImport GetStaged(int userId, string importId)
  {
    if (_cache.TryGetValue(CacheKey(userId, importId), out StagedImport? staged) && staged != null)
      return staged;
    throw new ImportException("This import has expired. Choose the file again.", 404);
  }

  private void EnforceRate(int userId, string kind, int limit)
  {
    var key = $"import:rate:{kind}:{userId}";
    var count = _cache.GetOrCreate(key, entry =>
    {
      entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
      return new int[1];
    })!;
    if (Interlocked.Increment(ref count[0]) > limit)
      throw new ImportException("You have run a lot of imports in the last hour. Try again later.", 429);
  }

  private static string SafeName(string? chosen, string fallback)
  {
    var name = string.IsNullOrWhiteSpace(chosen) ? fallback : chosen;
    name = name.Trim();
    return name.Length == 0 ? "Imported" : BuoyAI.Core.Utilities.SafeText.Truncate(name, 100);
  }

  private static string SafeText(string? text, int max)
  {
    var t = (text ?? string.Empty).Trim();
    return BuoyAI.Core.Utilities.SafeText.Truncate(t, max);
  }
}
