using System.Text.Json;
using BuoyAI.Infrastructure.Services.Ai;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Receipts;
using BuoyAI.Core.Utilities;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services.Receipts;

/// <summary>
/// Receipt scanning. <see cref="ScanAsync"/> reads a shared screenshot, photo or PDF while the user
/// waits, finds the bank charge it paid for and offers the split; <see cref="RematchAsync"/> (run by
/// <see cref="ReceiptRematchService"/>) retries receipts whose charge hadn't synced yet and notifies
/// when one matches. See docs/receipt_scanning.md.
/// </summary>
public class ReceiptService : IReceiptService
{
  public const string NotificationType = "receipt_split_ready";
  public const string ActionUrl = "/settings/receipts";

  private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

  /// <summary>System categories a store item can land in (no income, transfers or payments).</summary>
  private static readonly int[] ItemCategoryIds =
  {
    (int)TransactionCategory.Groceries,
    (int)TransactionCategory.DiningOut,
    (int)TransactionCategory.Shopping,
    (int)TransactionCategory.Healthcare,
    (int)TransactionCategory.PersonalCare,
    (int)TransactionCategory.Entertainment,
    (int)TransactionCategory.Education,
    (int)TransactionCategory.Gifts,
    (int)TransactionCategory.Transportation,
    (int)TransactionCategory.Travel,
    (int)TransactionCategory.Subscriptions,
    (int)TransactionCategory.Utilities,
    (int)TransactionCategory.Other,
  };

  private readonly BuoyAIDbContext _db;
  private readonly IEntitlementService _entitlements;
  private readonly ReceiptOptions _options;
  private readonly ITransactionService _transactions;
  private readonly INotificationService _notifications;
  private readonly IReceiptParser _parser;
  private readonly IFamilyAuthorizationService _authorization;
  private readonly ILogger<ReceiptService> _logger;

  public ReceiptService(
    BuoyAIDbContext db,
    IEntitlementService entitlements,
    ReceiptOptions options,
    ITransactionService transactions,
    INotificationService notifications,
    IReceiptParser parser,
    IFamilyAuthorizationService authorization,
    ILogger<ReceiptService> logger
  )
  {
    _db = db;
    _entitlements = entitlements;
    _options = options;
    _transactions = transactions;
    _notifications = notifications;
    _parser = parser;
    _authorization = authorization;
    _logger = logger;
  }

  public async Task<ReceiptStatusDto> GetStatusAsync(int userId)
  {
    var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
    return new ReceiptStatusDto
    {
      Blocker = BlockerFor(user),
      ReadyCount = await _db.ReceiptScans.CountAsync(r => r.UserId == userId && r.Status == ReceiptScanStatus.Ready),
      Stores = RetailerDirectory.All.Select(r => r.Name).ToList(),
    };
  }

  // ==================== Scanning ====================

  // A failed read is stored too, so it counts toward the daily limit like any other scan.
  private async Task RecordUnreadableAsync(int userId, DateTime receivedAt, int? hintTransactionId, CancellationToken ct)
  {
    _db.ChangeTracker.Clear();
    _db.ReceiptScans.Add(new ReceiptScan
    {
      UserId = userId,
      ReceivedAt = receivedAt,
      HintTransactionId = hintTransactionId,
      Status = ReceiptScanStatus.NotAReceipt,
      Error = "The receipt couldn't be read.",
    });
    await _db.SaveChangesAsync(CancellationToken.None);
  }

  public async Task<ReceiptScanDto> ScanAsync(
    int userId,
    IReadOnlyList<ReceiptFile> files,
    int? hintTransactionId,
    CancellationToken ct = default
  )
  {
    var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
      ?? throw new ReceiptScanBlockedException("unavailable", "User not found");
    var blocker = BlockerFor(user);
    if (blocker != null)
      throw new ReceiptScanBlockedException(blocker, BlockerMessage(blocker));
    var dayAgo = DateTime.UtcNow.AddDays(-1);
    if (await _db.ReceiptScans.CountAsync(r => r.UserId == userId && r.ReceivedAt > dayAgo, ct) >= _options.MaxPerDay)
      throw new ReceiptScanBlockedException("daily_limit", "That's a lot of receipts for one day. Try again tomorrow.");
    var checkedFiles = CheckFiles(files);

    var receivedAt = DateTime.UtcNow;
    var categories = await CategoryNamesAsync(user, ct);
    ParsedReceipt? parsed;
    try
    {
      using var usageScope = AiUsageScope.ForUser(userId);
      parsed = await _parser.ParseAsync(checkedFiles, receivedAt, categories);
    }
    catch
    {
      await RecordUnreadableAsync(userId, receivedAt, hintTransactionId, ct);
      throw;
    }
    if (parsed == null)
    {
      await RecordUnreadableAsync(userId, receivedAt, hintTransactionId, ct);
      throw new InvalidOperationException("The receipt couldn't be read.");
    }

    var scan = new ReceiptScan
    {
      UserId = userId,
      ReceivedAt = receivedAt,
      HintTransactionId = hintTransactionId,
      Status = ReceiptScanStatus.Unmatched,
    };
    _db.ReceiptScans.Add(scan);

    var retailer = RetailerDirectory.Resolve(parsed.Retailer);
    scan.Retailer = Truncate(retailer?.Name ?? parsed.Retailer, 60);
    if (parsed.IsReceipt && parsed.Total != null)
    {
      scan.OrderNumber = Truncate(parsed.OrderNumber, 80);
      scan.OrderDate = parsed.OrderDate.HasValue ? DateTime.SpecifyKind(parsed.OrderDate.Value.Date, DateTimeKind.Utc) : null;
      scan.Total = parsed.Total;
      scan.ParsedJson = JsonSerializer.Serialize(parsed, Json);
    }

    var problem =
      parsed.MultipleOrders ? "These screenshots show more than one order. Share each order's screenshots separately."
      : !parsed.IsReceipt || parsed.Total == null ? "This doesn't show a receipt with item prices and a total. For a long order, share all of its screenshots together."
      : parsed.Kind == "refund" ? "Refunds aren't split."
      : parsed.Items.Sum(i => i.Price) <= 0 ? "This doesn't show item prices, so it can't be split."
      : parsed.LooksIncomplete()
        ? $"The items shown add up to {CurrencyCodes.Format(parsed.ComputedTotal(), user.Currency)} but the order total is {CurrencyCodes.Format(parsed.Total!.Value, user.Currency)}, so part of the order looks missing. Share all of its screenshots together, top to bottom."
      : retailer == null ? "Buoy Money couldn't tell which store this receipt is from."
      : null;
    if (problem != null)
    {
      Finish(scan, ReceiptScanStatus.NotAReceipt, problem);
      await _db.SaveChangesAsync(ct);
      return ToDto(scan);
    }

    await _db.SaveChangesAsync(ct);
    // Saved before matching either way: a replaced receipt must stop holding on to its charge.
    var dropped = await SupersedeAsync(scan, parsed, ct);
    await _db.SaveChangesAsync(ct);
    if (dropped)
      return ToDto(scan);
    await TryMatchAsync(scan, parsed, retailer!, user, categories, notify: false, ct);
    _logger.LogInformation("Receipt scan {ScanId} for user {UserId}: {Status}", scan.Id, userId, scan.Status);
    return ToDto(scan);
  }

  /// <summary>One to four files, each a real image or PDF under the size limits; the type comes from the bytes.</summary>
  internal static List<ReceiptFile> CheckFiles(IReadOnlyList<ReceiptFile> files)
  {
    if (files.Count == 0)
      throw new ReceiptFileException("Add a screenshot, photo or PDF of the receipt.");
    if (files.Count > ReceiptFiles.MaxFiles)
      throw new ReceiptFileException($"Send at most {ReceiptFiles.MaxFiles} files for one receipt.");
    if (files.Sum(f => (long)f.Data.Length) > ReceiptFiles.MaxTotalBytes)
      throw new ReceiptFileException("Those files are too big together. Send fewer or smaller screenshots.");
    var result = new List<ReceiptFile>();
    foreach (var f in files)
    {
      if (f.Data.Length == 0 || f.Data.Length > ReceiptFiles.MaxFileBytes)
        throw new ReceiptFileException("Each file must be under 8 MB.");
      var type = ReceiptFiles.Sniff(f.Data)
        ?? throw new ReceiptFileException("Send a screenshot or photo (JPEG, PNG, WebP or HEIC) or a PDF.");
      result.Add(new ReceiptFile(type, f.Data));
    }
    return result;
  }

  /// <summary>
  /// Retries receipts whose charge hadn't synced yet, and gives up on ones older than the wait window.
  /// </summary>
  public async Task<int> RematchAsync(CancellationToken ct)
  {
    var cutoff = DateTime.UtcNow.AddDays(-_options.WaitDays);
    var expired = await _db
      .ReceiptScans.Where(r => r.Status == ReceiptScanStatus.Unmatched && r.ReceivedAt < cutoff)
      .ToListAsync(ct);
    foreach (var r in expired)
      Finish(r, ReceiptScanStatus.Expired, "No matching bank charge showed up.");
    if (expired.Count > 0)
      await _db.SaveChangesAsync(ct);

    var waiting = await _db
      .ReceiptScans.Where(r => r.Status == ReceiptScanStatus.Unmatched && r.ParsedJson != null)
      .OrderBy(r => r.UpdatedAt)
      .Take(100)
      .ToListAsync(ct);
    var matched = 0;
    foreach (var scan in waiting)
    {
      ct.ThrowIfCancellationRequested();
      var parsed = JsonSerializer.Deserialize<ParsedReceipt>(scan.ParsedJson!, Json);
      var retailer = RetailerDirectory.Resolve(scan.Retailer ?? parsed?.Retailer);
      var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == scan.UserId, ct);
      if (parsed == null || retailer == null || user == null)
        continue;
      var categories = await CategoryNamesAsync(user, ct);
      try
      {
        if (await TryMatchAsync(scan, parsed, retailer, user, categories, notify: true, ct))
          matched++;
      }
      catch (DbUpdateConcurrencyException ex)
      {
        // The user dismissed or replaced this one meanwhile. Detach only it: clearing the whole
        // tracker would detach the rest of this batch, whose later saves would then do nothing.
        foreach (var entry in ex.Entries)
          entry.State = EntityState.Detached;
      }
    }
    return matched;
  }

  private async Task<bool> TryMatchAsync(
    ReceiptScan scan,
    ParsedReceipt parsed,
    RetailerDirectory.Retailer retailer,
    User user,
    IReadOnlyDictionary<int, string> categories,
    bool notify,
    CancellationToken ct
  )
  {
    var candidates = await CandidatesAsync(user, scan, parsed, ct);
    var match = ReceiptMatcher.Match(parsed, retailer, candidates, scan.ReceivedAt, scan.HintTransactionId);
    scan.UpdatedAt = DateTime.UtcNow;
    var proposal = match == null ? null : ReceiptSplitPlanner.Propose(parsed, match, categories);
    if (proposal == null || proposal.Charges.Any(c => c.Splits.Count == 0))
    {
      await _db.SaveChangesAsync(ct);
      return false;
    }
    scan.ProposalJson = JsonSerializer.Serialize(proposal, Json);
    scan.TransactionId = proposal.Charges[0].TransactionId;
    scan.MatchedAt = DateTime.UtcNow;
    scan.Status = ReceiptScanStatus.Ready;
    await _db.SaveChangesAsync(ct);

    if (notify)
    {
      var parts = proposal.Charges.SelectMany(c => c.Splits).Select(s => s.CategoryId).Distinct().Count();
      await NotifyAsync(
        user.Id,
        "Receipt ready to split",
        $"{scan.Retailer} {CurrencyCodes.Format(proposal.Charges.Sum(c => c.Amount), user.Currency)}: {(parts == 1 ? "1 category" : $"{parts} categories")} ready. Tap to review."
      );
    }
    return true;
  }

  /// <summary>
  /// A newer receipt for the same order wins over an older one that hasn't been applied. If the
  /// order was already split, or a newer receipt for it was already read, this one is dropped.
  /// Returns true when this receipt was dropped.
  /// </summary>
  private async Task<bool> SupersedeAsync(ReceiptScan scan, ParsedReceipt parsed, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(scan.OrderNumber))
      return false;
    var others = await _db
      .ReceiptScans.Where(r =>
        r.UserId == scan.UserId
        && r.Id != scan.Id
        && r.OrderNumber == scan.OrderNumber
        && r.Retailer == scan.Retailer
        && (r.Status == ReceiptScanStatus.Applied || r.Status == ReceiptScanStatus.Ready || r.Status == ReceiptScanStatus.Unmatched)
      )
      .ToListAsync(ct);
    if (others.Count == 0)
      return false;
    if (others.Any(o => o.Status == ReceiptScanStatus.Applied))
    {
      Finish(scan, ReceiptScanStatus.Dismissed, "This order was already split from an earlier receipt.");
      return true;
    }
    if (others.Any(o => o.ReceivedAt > scan.ReceivedAt))
    {
      Finish(scan, ReceiptScanStatus.Dismissed, "A newer receipt for this order was already read.");
      return true;
    }
    foreach (var o in others)
      Finish(o, ReceiptScanStatus.Dismissed, "Replaced by a newer receipt for this order.");
    return false;
  }

  private async Task<List<ReceiptCandidate>> CandidatesAsync(User user, ReceiptScan scan, ParsedReceipt parsed, CancellationToken ct)
  {
    var anchor = (parsed.OrderDate ?? scan.ReceivedAt).Date;
    var from = DateTime.SpecifyKind(anchor.AddDays(-ReceiptMatcher.DaysBefore), DateTimeKind.Utc);
    var later = scan.ReceivedAt.Date > anchor ? scan.ReceivedAt.Date : anchor;
    var to = DateTime.SpecifyKind(later.AddDays(ReceiptMatcher.DaysAfter + 1), DateTimeKind.Utc);
    var familyId = user.FamilyId;
    var hint = scan.HintTransactionId;

    var taken = await TakenTransactionIdsAsync(user.Id, scan.Id, ct);
    var rows = await _db
      .Transactions.AsNoTracking()
      .Where(t => t.Account.UserId == user.Id || (familyId.HasValue && t.Account.FamilyId == familyId))
      .Where(t => t.Type == TransactionType.Debit && ((t.Date >= from && t.Date < to) || t.Id == hint))
      // Earmarked to a savings goal: splitting clears the earmark, so these are left alone.
      .Where(t => t.SavingsGoalId == null)
      .Select(t => new ReceiptCandidate(
        t.Id,
        t.Amount,
        t.Type == TransactionType.Debit,
        t.Date,
        t.MerchantName,
        t.Description,
        t.IsPending,
        t.IsSplit,
        t.ParentTransactionId != null,
        t.IsoCurrencyCode
      ))
      .ToListAsync(ct);
    return rows.Where(r => !taken.Contains(r.Id)).ToList();
  }

  /// <summary>Charges another of this user's receipts already offered or applied a split for.</summary>
  private async Task<HashSet<int>> TakenTransactionIdsAsync(int userId, int exceptScanId, CancellationToken ct)
  {
    var proposals = await _db
      .ReceiptScans.AsNoTracking()
      .Where(r =>
        r.UserId == userId
        && r.Id != exceptScanId
        && r.ProposalJson != null
        && (r.Status == ReceiptScanStatus.Ready || r.Status == ReceiptScanStatus.Applied)
      )
      .Select(r => r.ProposalJson!)
      .ToListAsync(ct);
    return proposals
      .Select(p => JsonSerializer.Deserialize<ReceiptProposal>(p, Json))
      .Where(p => p != null)
      .SelectMany(p => p!.Charges.Select(c => c.TransactionId))
      .ToHashSet();
  }

  // ==================== Reading back and applying ====================

  public async Task<List<ReceiptScanDto>> GetScansAsync(int userId, int limit = 30)
  {
    var rows = await _db
      .ReceiptScans.AsNoTracking()
      .Where(r => r.UserId == userId)
      .OrderByDescending(r => r.ReceivedAt)
      .Take(Math.Clamp(limit, 1, 100))
      .ToListAsync();
    return rows.Select(ToDto).ToList();
  }

  public async Task<ReceiptScanDto?> GetScanAsync(int userId, int scanId)
  {
    var row = await _db.ReceiptScans.AsNoTracking().FirstOrDefaultAsync(r => r.Id == scanId && r.UserId == userId);
    return row == null ? null : ToDto(row);
  }

  public async Task<ReceiptScanDto?> GetScanForTransactionAsync(int userId, int transactionId)
  {
    var open = await _db
      .ReceiptScans.AsNoTracking()
      .Where(r =>
        r.UserId == userId
        && (r.Status == ReceiptScanStatus.Ready || r.Status == ReceiptScanStatus.Applied)
        && r.ProposalJson != null
      )
      .OrderByDescending(r => r.ReceivedAt)
      .Take(200)
      .ToListAsync();
    var row = open.FirstOrDefault(r =>
      r.TransactionId == transactionId || ProposalOf(r)?.Charges.Any(c => c.TransactionId == transactionId) == true
    );
    return row == null ? null : ToDto(row);
  }

  public async Task<ReceiptScanDto?> ApplyAsync(int userId, int scanId)
  {
    var scan = await _db.ReceiptScans.FirstOrDefaultAsync(r => r.Id == scanId && r.UserId == userId);
    // An Apply that died part way (the process restarted) is claimable again after a few minutes.
    var stuck = scan?.Status == ReceiptScanStatus.Applying && scan.UpdatedAt < DateTime.UtcNow.AddMinutes(-10);
    if (scan == null || (scan.Status != ReceiptScanStatus.Ready && !stuck))
      return null;

    // Claim it first. Status is a concurrency token, so if another Apply claimed it in the
    // meantime this save fails and this one stops before touching any transaction.
    scan.Status = ReceiptScanStatus.Applying;
    scan.UpdatedAt = DateTime.UtcNow;
    try
    {
      await _db.SaveChangesAsync();
    }
    catch (DbUpdateConcurrencyException)
    {
      _db.ChangeTracker.Clear();
      return null;
    }

    await ApplyInternalAsync(scan);
    return await GetScanAsync(userId, scanId);
  }

  public async Task<ReceiptScanDto?> DismissAsync(int userId, int scanId)
  {
    var scan = await _db.ReceiptScans.FirstOrDefaultAsync(r => r.Id == scanId && r.UserId == userId);
    if (scan == null || !ReceiptScanStatus.Open.Contains(scan.Status))
      return null;
    Finish(scan, ReceiptScanStatus.Dismissed, null);
    await _db.SaveChangesAsync();
    return ToDto(scan);
  }

  private async Task ApplyInternalAsync(ReceiptScan scan)
  {
    var proposal = ProposalOf(scan);
    var parsed = scan.ParsedJson == null ? null : JsonSerializer.Deserialize<ParsedReceipt>(scan.ParsedJson, Json);
    if (proposal == null || parsed == null)
    {
      Finish(scan, ReceiptScanStatus.Ready, "The split couldn't be applied.");
      await _db.SaveChangesAsync();
      return;
    }

    var scanId = scan.Id;
    var userId = scan.UserId;
    var categories = await CategoryNamesAsync(await _db.Users.FirstAsync(u => u.Id == userId), CancellationToken.None);
    string? error = null;
    var afterCommit = new List<Func<Task>>();
    var committed = false;

    // Every charge split and the scan's Applied status commit together in one database transaction
    // (under the execution strategy, so a transient failure re-runs the whole block). A crash or a
    // failure on any charge rolls all of it back: no half split order, and a retry starts clean.
    var strategy = _db.Database.CreateExecutionStrategy();
    try
    {
      await strategy.ExecuteAsync(async () =>
      {
        error = null;
        afterCommit.Clear();
        _db.ChangeTracker.Clear();
        // A retry after a commit of unknown outcome: if it landed, the scan is already Applied.
        if (await _db.ReceiptScans.AsNoTracking().AnyAsync(r => r.Id == scanId && r.Status == ReceiptScanStatus.Applied))
        {
          committed = true;
          return;
        }
        await using var dbTx = await _db.Database.BeginTransactionAsync();
        error = await ApplyChargesAsync(proposal, parsed, categories, userId, scanId, afterCommit);
        if (error != null)
        {
          await dbTx.RollbackAsync();
          return;
        }
        var applied = await _db.ReceiptScans.FirstAsync(r => r.Id == scanId);
        applied.Status = ReceiptScanStatus.Applied;
        applied.AppliedAt = DateTime.UtcNow;
        applied.Error = null;
        applied.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await dbTx.CommitAsync();
        committed = true;
      });
    }
    catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException or DbUpdateException)
    {
      _logger.LogWarning(ex, "Applying receipt {ScanId} failed; nothing was changed", scanId);
      error = "The split couldn't be applied.";
    }

    if (committed)
      await RunAfterCommitAsync(afterCommit, scanId);

    if (error != null)
    {
      // Nothing was committed. Release the claim so the user can try again.
      _db.ChangeTracker.Clear();
      var row = await _db.ReceiptScans.FirstAsync(r => r.Id == scanId);
      row.Status = ReceiptScanStatus.Ready;
      row.Error = error;
      row.UpdatedAt = DateTime.UtcNow;
      await _db.SaveChangesAsync();
    }
  }

  /// <summary>
  /// Runs the notification, gamification and embedding steps the charge updates deferred, once the
  /// transaction has committed. Each is best effort: the split is already saved.
  /// </summary>
  private async Task RunAfterCommitAsync(List<Func<Task>> steps, int scanId)
  {
    foreach (var step in steps)
    {
      try
      {
        _db.ChangeTracker.Clear();
        await step();
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "A post-commit step for receipt {ScanId} failed", scanId);
      }
    }
  }

  /// <summary>
  /// Checks every charge, then splits (or recategorizes) each one. Runs inside the caller's
  /// transaction. Returns a message for the user when it can't be applied, else null.
  /// </summary>
  private async Task<string?> ApplyChargesAsync(
    ReceiptProposal proposal,
    ParsedReceipt parsed,
    IReadOnlyDictionary<int, string> categories,
    int userId,
    int scanId,
    ICollection<Func<Task>> afterCommit
  )
  {
    // Check every charge before changing any.
    var plan = new List<(ProposedCharge Charge, List<PlannedSplit> Splits, string? UserNotes)>();
    foreach (var charge in proposal.Charges)
    {
      var tx = await _transactions.GetTransactionByIdAsync(charge.TransactionId, userId);
      if (tx == null)
        return "A matched charge is gone or you can't see it.";
      try
      {
        await _authorization.ValidateTransactionAccessOrThrowAsync(userId, charge.TransactionId, ResourcePermission.Edit);
      }
      catch (UnauthorizedAccessException)
      {
        return "You can view this charge but not edit it.";
      }
      if (tx.IsSplit || tx.ParentTransactionId.HasValue)
        // Split since the offer (by hand, or another receipt): applying now would split it twice.
        return "This charge was already split, so the receipt can't be applied to it.";
      // The charge may have changed since the offer (rare): re-plan for today's amount.
      var splits = Math.Abs(tx.Amount) == charge.Amount ? charge.Splits : ReceiptSplitPlanner.Plan(parsed, tx.Amount, categories);
      if (splits.Count == 0)
        return "The split couldn't be applied.";
      plan.Add((charge, splits, tx.UserNotes));
    }

    foreach (var (charge, splits, userNotes) in plan)
    {
      if (splits.Count >= 2)
      {
        await _transactions.SplitTransactionAsync(
          charge.TransactionId,
          new SplitTransactionRequest
          {
            Splits = splits
              .Select(s => new TransactionSplitItem { Amount = s.Amount, CategoryId = s.CategoryId, Note = s.Note() })
              .ToList(),
          },
          userId
        );
      }
      else
      {
        await _transactions.UpdateTransactionCategoryDeferringSideEffectsAsync(
          charge.TransactionId,
          new UpdateTransactionCategoryRequest { CategoryId = splits[0].CategoryId },
          userId,
          afterCommit
        );
        _db.ChangeTracker.Clear();
        if (string.IsNullOrWhiteSpace(userNotes))
          await _transactions.UpdateTransactionNotesAsync(charge.TransactionId, splits[0].Note(), userId);
      }
      // Each service call loads its own copies; a clean tracker keeps the next one from tripping
      // over the last one's Category entities.
      _db.ChangeTracker.Clear();
    }
    return null;
  }

  private async Task NotifyAsync(int userId, string title, string message)
  {
    try
    {
      await _notifications.CreateNotificationAsync(
        userId,
        new CreateNotificationRequest
        {
          Type = NotificationType,
          Title = title,
          Message = message,
          ActionUrl = ActionUrl,
          ActionText = "Review",
        }
      );
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Receipt notification for user {UserId} failed", userId);
    }
  }

  // ==================== Helpers ====================

  /// <summary>Item categories for this user: the store-type system ones plus their own expense categories.</summary>
  internal async Task<IReadOnlyDictionary<int, string>> CategoryNamesAsync(User user, CancellationToken ct)
  {
    var names = ItemCategoryIds.ToDictionary(id => id, id => ((TransactionCategory)id).GetDisplayName());
    var familyId = user.FamilyId;
    var custom = await _db
      .Categories.AsNoTracking()
      .Where(c =>
        c.Id >= 1000 && !c.IsSystem && !c.IsIncome && (c.UserId == user.Id || (familyId.HasValue && c.FamilyId == familyId))
      )
      .OrderBy(c => c.Name)
      .Select(c => new { c.Id, c.Name })
      .Take(80)
      .ToListAsync(ct);
    foreach (var c in custom)
      names[c.Id] = c.Name;
    return names;
  }

  private static ReceiptProposal? ProposalOf(ReceiptScan r)
  {
    if (r.ProposalJson == null)
      return null;
    try
    {
      return JsonSerializer.Deserialize<ReceiptProposal>(r.ProposalJson, Json);
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private static void Finish(ReceiptScan r, string status, string? error)
  {
    r.Status = status;
    r.Error = Truncate(error, 500);
    r.UpdatedAt = DateTime.UtcNow;
  }

  private string? BlockerFor(User? user)
  {
    if (user == null)
      return "unavailable";
    if (_options.RequirePremium && !_entitlements.HasPremiumAccess(user))
      return "premium_required";
    if (!AiConsent.IsCurrent(user))
      return "ai_consent_required";
    return null;
  }

  private static string BlockerMessage(string blocker) =>
    blocker switch
    {
      "premium_required" => "Receipt scanning is part of Premium.",
      "ai_consent_required" => "Turn on AI features first. Receipts are read by Google Gemini.",
      _ => "Receipt scanning isn't available.",
    };

  private static ReceiptScanDto ToDto(ReceiptScan r)
  {
    var itemCount = 0;
    if (r.ParsedJson != null)
    {
      try
      {
        itemCount = JsonSerializer.Deserialize<ParsedReceipt>(r.ParsedJson, Json)?.Items.Count ?? 0;
      }
      catch (JsonException) { }
    }
    return new ReceiptScanDto
    {
      Id = r.Id,
      Status = r.Status,
      Retailer = r.Retailer,
      OrderNumber = r.OrderNumber,
      OrderDate = r.OrderDate,
      Total = r.Total,
      ReceivedAt = r.ReceivedAt,
      AppliedAt = r.AppliedAt,
      TransactionId = r.TransactionId,
      Message =
        r.Status == ReceiptScanStatus.Unmatched && r.Error == null
          ? "The charge isn't in from your bank yet. Buoy Money will match it when it arrives and let you know."
          : r.Error,
      ItemCount = itemCount,
      Proposal = r.Status is ReceiptScanStatus.Ready or ReceiptScanStatus.Applied ? ProposalOf(r) : null,
    };
  }

  private static string? Truncate(string? s, int max) => s == null ? null : SafeText.Truncate(s, max);
}
