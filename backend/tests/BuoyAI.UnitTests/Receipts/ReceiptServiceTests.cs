using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Receipts;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services.Receipts;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Receipts;

public class ReceiptServiceTests : IDisposable
{
  private static readonly DateTime Today = DateTime.UtcNow.Date;

  private readonly SqliteConnection _connection = new("DataSource=:memory:;Foreign Keys=False");
  private readonly BuoyAIDbContext _db;
  private readonly Mock<IReceiptParser> _parser = new();
  private readonly Mock<ITransactionService> _transactions = new();
  private readonly Mock<INotificationService> _notifications = new();
  private readonly Mock<IFamilyAuthorizationService> _authorization = new();
  private readonly ReceiptOptions _options = new();

  public ReceiptServiceTests()
  {
    // SQLite (not the in-memory provider) because Apply now runs in a real database transaction.
    _connection.Open();
    _db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>().UseSqlite(_connection).Options);
    _db.Database.EnsureCreated();
    _notifications
      .Setup(n => n.CreateNotificationAsync(It.IsAny<int>(), It.IsAny<CreateNotificationRequest>()))
      .ReturnsAsync(new NotificationDto());
  }

  public void Dispose()
  {
    _db.Dispose();
    _connection.Dispose();
  }

  private ReceiptService Service() =>
    new(
      _db,
      BuoyAI.UnitTests.Services.TestEntitlements.Create(_db, new ConfigurationBuilder().Build()),
      _options,
      _transactions.Object,
      _notifications.Object,
      _parser.Object,
      _authorization.Object,
      NullLogger<ReceiptService>.Instance
    );

  private static readonly byte[] Png = MakePng();

  private static byte[] MakePng()
  {
    var b = new byte[2000];
    new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
    return b;
  }

  private static List<ReceiptFile> Shot() => new() { new ReceiptFile("image/png", Png) };

  private async Task<User> SeedUserAsync(bool premium = true, bool consent = true)
  {
    var user = new User
    {
      Id = 1,
      Email = "dana@example.com",
      Username = "dana",
      SubscriptionTier = premium ? SubscriptionTier.Premium : null,
      AiConsentGrantedAt = consent ? DateTime.UtcNow.AddDays(-1) : null,
      AiConsentVersion = consent ? AiConsent.CurrentVersion : null,
    };
    _db.Users.Add(user);
    _db.Accounts.Add(new Account { Id = 10, UserId = 1, Name = "Checking", PlaidAccountId = "acc-10" });
    await _db.SaveChangesAsync();
    return user;
  }

  private async Task SeedChargeAsync(int id, decimal amount, string merchant = "Walmart", int daysAgo = 1, bool pending = false)
  {
    _db.Transactions.Add(new Transaction
    {
      Id = id,
      AccountId = 10,
      Amount = amount,
      Type = TransactionType.Debit,
      MerchantName = merchant,
      Description = merchant.ToUpperInvariant(),
      Date = Today.AddDays(-daysAgo),
      IsPending = pending,
      PlaidTransactionId = $"tx-{id}",
    });
    await _db.SaveChangesAsync();
  }

  private static ParsedReceipt WalmartReceipt(decimal total = 60m, string? order = "2000123") =>
    new()
    {
      IsReceipt = true,
      Retailer = "walmart",
      OrderNumber = order,
      OrderDate = Today.AddDays(-2),
      Kind = "delivered",
      Total = total,
      Items =
      {
        new ParsedReceiptItem { Name = "Milk", Price = total * 2 / 3, CategoryId = 20 },
        new ParsedReceiptItem { Name = "Paper towels", Price = total / 3, CategoryId = 24 },
      },
    };

  private void ParserReturns(ParsedReceipt? receipt) =>
    _parser
      .Setup(p => p.ParseAsync(It.IsAny<IReadOnlyList<ReceiptFile>>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyDictionary<int, string>>()))
      .ReturnsAsync(receipt);

  private void Editable(int id, decimal amount) =>
    _transactions.Setup(t => t.GetTransactionByIdAsync(id, 1)).ReturnsAsync(new TransactionDto { Id = id, Amount = -amount });

  // ==================== Who can scan ====================

  [Theory]
  [InlineData(false, true, "premium_required")]
  [InlineData(true, false, "ai_consent_required")]
  public async Task It_needs_premium_and_ai_consent(bool premium, bool consent, string reason)
  {
    await SeedUserAsync(premium, consent);
    (await Service().GetStatusAsync(1)).Blocker.Should().Be(reason);
    var act = () => Service().ScanAsync(1, Shot(), null);
    (await act.Should().ThrowAsync<ReceiptScanBlockedException>()).Which.Reason.Should().Be(reason);
    _parser.VerifyNoOtherCalls();
  }

  [Fact]
  public async Task There_is_a_daily_limit()
  {
    await SeedUserAsync();
    _options.MaxPerDay = 1;
    ParserReturns(new ParsedReceipt { IsReceipt = false });
    await Service().ScanAsync(1, Shot(), null);
    var act = () => Service().ScanAsync(1, Shot(), null);
    (await act.Should().ThrowAsync<ReceiptScanBlockedException>()).Which.Reason.Should().Be("daily_limit");
  }

  [Fact]
  public async Task A_receipt_that_cannot_be_read_still_counts_toward_the_daily_limit()
  {
    await SeedUserAsync();
    _options.MaxPerDay = 1;
    ParserReturns(null);
    var first = () => Service().ScanAsync(1, Shot(), null);
    await first.Should().ThrowAsync<InvalidOperationException>();
    var second = () => Service().ScanAsync(1, Shot(), null);
    (await second.Should().ThrowAsync<ReceiptScanBlockedException>()).Which.Reason.Should().Be("daily_limit");
  }

  // ==================== Files ====================

  [Fact]
  public void Files_are_typed_by_their_bytes_not_their_label()
  {
    var checkedFiles = ReceiptService.CheckFiles(new[] { new ReceiptFile("application/octet-stream", Png) });
    checkedFiles.Single().MimeType.Should().Be("image/png");

    var exe = new byte[2000];
    exe[0] = (byte)'M';
    exe[1] = (byte)'Z';
    var act = () => ReceiptService.CheckFiles(new[] { new ReceiptFile("image/png", exe) });
    act.Should().Throw<ReceiptFileException>();
    ((Action)(() => ReceiptService.CheckFiles(Array.Empty<ReceiptFile>()))).Should().Throw<ReceiptFileException>();
    ((Action)(() => ReceiptService.CheckFiles(Enumerable.Repeat(new ReceiptFile("image/png", Png), 9).ToList())))
      .Should().Throw<ReceiptFileException>();
    ((Action)(() => ReceiptService.CheckFiles(new[] { new ReceiptFile("image/png", new byte[9 * 1024 * 1024]) })))
      .Should().Throw<ReceiptFileException>();
  }

  [Theory]
  [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "application/pdf")]
  [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
  [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
  [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 }, "image/heic")]
  [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D }, null)] // MP4 (isom)
  [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66 }, null)] // AVIF
  [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, null)]
  public void Types_are_sniffed(byte[] header, string? type)
  {
    ReceiptFiles.Sniff(header).Should().Be(type);
  }

  // ==================== Reading and matching ====================

  [Fact]
  public async Task A_receipt_matching_a_charge_comes_back_as_a_split_right_away()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m);
    ParserReturns(WalmartReceipt());

    var scan = await Service().ScanAsync(1, Shot(), null);

    scan.Status.Should().Be(ReceiptScanStatus.Ready);
    scan.TransactionId.Should().Be(100);
    scan.Retailer.Should().Be("Walmart");
    scan.Proposal!.Charges.Single().Splits.Select(s => (s.CategoryId, s.Amount)).Should().Equal((20, 40m), (24, 20m));
    // The user is looking at the result already: no notification for an immediate match.
    _notifications.VerifyNoOtherCalls();
  }

  [Fact]
  public async Task Scanning_from_a_transaction_matches_that_charge()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m);
    await SeedChargeAsync(101, 60m, merchant: "POS DEBIT 8831", daysAgo: 1);
    ParserReturns(WalmartReceipt());

    var scan = await Service().ScanAsync(1, Shot(), hintTransactionId: 101);

    scan.TransactionId.Should().Be(101);
  }

  [Fact]
  public async Task A_receipt_waits_for_its_charge_and_notifies_when_it_matches()
  {
    await SeedUserAsync();
    ParserReturns(WalmartReceipt());
    var scan = await Service().ScanAsync(1, Shot(), null);
    scan.Status.Should().Be(ReceiptScanStatus.Unmatched);
    scan.Message.Should().Contain("isn't in from your bank yet");

    await SeedChargeAsync(100, 60m);
    (await Service().RematchAsync(CancellationToken.None)).Should().Be(1);
    _db.ChangeTracker.Clear();
    (await _db.ReceiptScans.SingleAsync()).Status.Should().Be(ReceiptScanStatus.Ready);
    _notifications.Verify(
      n => n.CreateNotificationAsync(1, It.Is<CreateNotificationRequest>(r => r.Type == ReceiptService.NotificationType && r.Message.Contains("2 categories"))),
      Times.Once
    );
  }

  [Fact]
  public async Task A_receipt_that_never_finds_its_charge_expires()
  {
    await SeedUserAsync();
    ParserReturns(WalmartReceipt());
    var scan = await Service().ScanAsync(1, Shot(), null);
    var row = await _db.ReceiptScans.SingleAsync(r => r.Id == scan.Id);
    row.ReceivedAt = DateTime.UtcNow.AddDays(-15);
    await _db.SaveChangesAsync();

    await Service().RematchAsync(CancellationToken.None);
    _db.ChangeTracker.Clear();
    (await _db.ReceiptScans.SingleAsync()).Status.Should().Be(ReceiptScanStatus.Expired);
  }

  [Fact]
  public async Task A_pending_hold_is_not_matched()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m, pending: true);
    ParserReturns(WalmartReceipt());
    (await Service().ScanAsync(1, Shot(), null)).Status.Should().Be(ReceiptScanStatus.Unmatched);
  }

  [Fact]
  public async Task Something_that_is_not_a_receipt_says_so()
  {
    await SeedUserAsync();
    ParserReturns(new ParsedReceipt { IsReceipt = false });
    var scan = await Service().ScanAsync(1, Shot(), null);
    scan.Status.Should().Be(ReceiptScanStatus.NotAReceipt);
    scan.Message.Should().Contain("share all of its screenshots together");
  }

  [Fact]
  public async Task Screenshots_of_two_different_orders_are_sent_back()
  {
    await SeedUserAsync();
    var receipt = WalmartReceipt();
    receipt.MultipleOrders = true;
    ParserReturns(receipt);
    var scan = await Service().ScanAsync(1, new List<ReceiptFile> { Shot()[0], Shot()[0] }, null);
    scan.Status.Should().Be(ReceiptScanStatus.NotAReceipt);
    scan.Message.Should().Contain("more than one order");
  }

  [Fact]
  public async Task A_long_order_missing_a_stretch_of_items_is_sent_back_with_the_numbers()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 129.95m);
    ParserReturns(WalmartReceipt(total: 129.95m) is var r ? Shrink(r) : null);
    var scan = await Service().ScanAsync(1, Shot(), null);
    scan.Status.Should().Be(ReceiptScanStatus.NotAReceipt);
    scan.Message.Should().Contain("$51.00").And.Contain("$129.95").And.Contain("missing");
  }

  /// <summary>The same order with only the first screenful of items read.</summary>
  private static ParsedReceipt Shrink(ParsedReceipt r)
  {
    r.Items = new() { new ParsedReceiptItem { Name = "Pizza sauce", Price = 11m, CategoryId = 20 }, new ParsedReceiptItem { Name = "Litter box", Price = 40m, CategoryId = 24 } };
    return r;
  }

  [Fact]
  public async Task Fees_and_tips_count_toward_the_total()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 70m);
    var receipt = WalmartReceipt(total: 70m);
    receipt.Items = new() { new ParsedReceiptItem { Name = "Milk", Price = 40m, CategoryId = 20 }, new ParsedReceiptItem { Name = "Towels", Price = 20m, CategoryId = 24 } };
    receipt.Fees = 10m; // delivery fee and driver tip
    ParserReturns(receipt);
    var scan = await Service().ScanAsync(1, Shot(), null);
    scan.Status.Should().Be(ReceiptScanStatus.Ready);
    scan.Proposal!.Confident.Should().BeTrue();
    scan.Proposal.Charges.Single().Splits.Sum(s => s.Amount).Should().Be(70m);
  }

  [Fact]
  public async Task An_unreadable_reply_throws_so_the_user_can_retry_and_is_kept_as_unreadable()
  {
    await SeedUserAsync();
    ParserReturns(null);
    var act = () => Service().ScanAsync(1, Shot(), null);
    await act.Should().ThrowAsync<InvalidOperationException>();
    // Kept (not a receipt, nothing to apply) so the paid read counts toward the daily limit.
    var row = _db.ReceiptScans.Should().ContainSingle().Subject;
    row.Status.Should().Be(ReceiptScanStatus.NotAReceipt);
    row.Error.Should().Contain("couldn't be read");
  }

  [Fact]
  public async Task A_newer_receipt_for_the_same_order_replaces_an_unapplied_one()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 55m);
    ParserReturns(WalmartReceipt(total: 60m));
    var first = await Service().ScanAsync(1, Shot(), null);
    first.Status.Should().Be(ReceiptScanStatus.Ready); // scaled: $55 is within 15% of $60

    ParserReturns(WalmartReceipt(total: 55m));
    var second = await Service().ScanAsync(1, Shot(), null);

    second.Status.Should().Be(ReceiptScanStatus.Ready);
    (await _db.ReceiptScans.SingleAsync(r => r.Id == first.Id)).Status.Should().Be(ReceiptScanStatus.Dismissed);
  }

  [Fact]
  public async Task A_second_receipt_for_an_already_split_order_is_dropped()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m);
    ParserReturns(WalmartReceipt());
    var first = await Service().ScanAsync(1, Shot(), null);
    var row = await _db.ReceiptScans.SingleAsync(r => r.Id == first.Id);
    row.Status = ReceiptScanStatus.Applied;
    await _db.SaveChangesAsync();

    (await Service().ScanAsync(1, Shot(), null)).Status.Should().Be(ReceiptScanStatus.Dismissed);
  }

  [Fact]
  public async Task A_charge_already_offered_to_one_receipt_is_not_offered_to_another()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m);
    ParserReturns(WalmartReceipt(order: "A1"));
    (await Service().ScanAsync(1, Shot(), null)).Status.Should().Be(ReceiptScanStatus.Ready);
    ParserReturns(WalmartReceipt(order: "B2"));
    (await Service().ScanAsync(1, Shot(), null)).Status.Should().Be(ReceiptScanStatus.Unmatched);
  }

  // ==================== Applying ====================

  [Fact]
  public async Task Applying_splits_the_charge_with_item_notes()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m);
    ParserReturns(WalmartReceipt());
    var scan = await Service().ScanAsync(1, Shot(), null);
    Editable(100, 60m);
    SplitTransactionRequest? sent = null;
    _transactions
      .Setup(t => t.SplitTransactionAsync(100, It.IsAny<SplitTransactionRequest>(), 1))
      .Callback<int, SplitTransactionRequest, int>((_, r, _) => sent = r)
      .ReturnsAsync(new TransactionDto());

    var applied = await Service().ApplyAsync(1, scan.Id);

    applied!.Status.Should().Be(ReceiptScanStatus.Applied);
    sent!.Splits.Select(s => (s.CategoryId, s.Amount, s.Note)).Should().Equal((20, 40m, "Milk"), (24, 20m, "Paper towels"));
    (await Service().ApplyAsync(1, scan.Id)).Should().BeNull(); // only once
  }

  [Fact]
  public async Task Apply_changes_nothing_when_a_charge_cannot_be_edited()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m);
    ParserReturns(WalmartReceipt());
    var scan = await Service().ScanAsync(1, Shot(), null);
    Editable(100, 60m);
    _authorization
      .Setup(a => a.ValidateTransactionAccessOrThrowAsync(1, 100, ResourcePermission.Edit))
      .ThrowsAsync(new UnauthorizedAccessException("read only"));

    var result = await Service().ApplyAsync(1, scan.Id);

    result!.Status.Should().Be(ReceiptScanStatus.Ready);
    result.Message.Should().Contain("not edit");
    _transactions.Verify(t => t.SplitTransactionAsync(It.IsAny<int>(), It.IsAny<SplitTransactionRequest>(), It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task Apply_fails_when_the_charge_was_split_since_the_offer()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m);
    ParserReturns(WalmartReceipt());
    var scan = await Service().ScanAsync(1, Shot(), null);
    _transactions.Setup(t => t.GetTransactionByIdAsync(100, 1))
      .ReturnsAsync(new TransactionDto { Id = 100, Amount = -60m, IsSplit = true });

    var result = await Service().ApplyAsync(1, scan.Id);

    result!.Status.Should().Be(ReceiptScanStatus.Ready);
    result.Message.Should().Contain("already split");
    _transactions.Verify(t => t.SplitTransactionAsync(It.IsAny<int>(), It.IsAny<SplitTransactionRequest>(), It.IsAny<int>()), Times.Never);
  }

  /// <summary>A transaction service whose split really writes child rows, as the real one does.</summary>
  private void SplitsWriteRows(Func<int, bool>? failFor = null)
  {
    _transactions
      .Setup(t => t.SplitTransactionAsync(It.IsAny<int>(), It.IsAny<SplitTransactionRequest>(), 1))
      .Returns(async (int id, SplitTransactionRequest r, int _) =>
      {
        var parent = await _db.Transactions.SingleAsync(t => t.Id == id);
        parent.IsSplit = true;
        foreach (var s in r.Splits)
          _db.Transactions.Add(new Transaction
          {
            AccountId = parent.AccountId, Amount = -s.Amount, Type = parent.Type, Date = parent.Date,
            ParentTransactionId = id, CategoryId = s.CategoryId, PlaidTransactionId = $"split-{id}-{Guid.NewGuid():N}",
          });
        await _db.SaveChangesAsync();
        if (failFor?.Invoke(id) == true)
          throw new InvalidOperationException("db hiccup");
        return new TransactionDto();
      });
  }

  private async Task<ReceiptScanDto> TwoChargeAmazonScanAsync()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 30m, merchant: "Amazon");
    await SeedChargeAsync(101, 30m, merchant: "Amazon", daysAgo: 0);
    var amazon = WalmartReceipt(total: 60m);
    amazon.Retailer = "amazon";
    amazon.Charges = new List<decimal> { 30m, 30m };
    ParserReturns(amazon);
    var scan = await Service().ScanAsync(1, Shot(), null);
    scan.Status.Should().Be(ReceiptScanStatus.Ready);
    _transactions.Setup(t => t.GetTransactionByIdAsync(It.IsAny<int>(), 1))
      .ReturnsAsync((int id, int _) => new TransactionDto { Id = id, Amount = -30m });
    return scan;
  }

  private async Task<(int Splits, int Children)> SplitStateAsync()
  {
    _db.ChangeTracker.Clear();
    return (
      await _db.Transactions.CountAsync(t => t.IsSplit),
      await _db.Transactions.CountAsync(t => t.ParentTransactionId != null)
    );
  }

  [Fact]
  public async Task A_failure_after_the_first_charge_leaves_no_split_behind()
  {
    var scan = await TwoChargeAmazonScanAsync();
    SplitsWriteRows(failFor: id => id == 101);

    var result = await Service().ApplyAsync(1, scan.Id);

    result!.Status.Should().Be(ReceiptScanStatus.Ready);
    result.Message.Should().NotBeNull();
    (await SplitStateAsync()).Should().Be((0, 0));
    // No hand-written undo: the database rolled the first charge back.
    _transactions.Verify(t => t.UnsplitTransactionAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
  }

  private async Task<ReceiptScanDto> OneCategoryTwoChargeScanAsync()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 30m, merchant: "Amazon");
    await SeedChargeAsync(101, 30m, merchant: "Amazon", daysAgo: 0);
    var amazon = WalmartReceipt(total: 60m);
    amazon.Retailer = "amazon";
    amazon.Charges = new List<decimal> { 30m, 30m };
    foreach (var item in amazon.Items)
      item.CategoryId = 20;
    ParserReturns(amazon);
    var scan = await Service().ScanAsync(1, Shot(), null);
    scan.Status.Should().Be(ReceiptScanStatus.Ready);
    _transactions.Setup(t => t.GetTransactionByIdAsync(It.IsAny<int>(), 1))
      .ReturnsAsync((int id, int _) => new TransactionDto { Id = id, Amount = -30m });
    return scan;
  }

  private void DeferringRecategorizeQueues(List<int> ran, Func<int, bool>? failFor = null) =>
    _transactions
      .Setup(t => t.UpdateTransactionCategoryDeferringSideEffectsAsync(
        It.IsAny<int>(), It.IsAny<UpdateTransactionCategoryRequest>(), 1, It.IsAny<ICollection<Func<Task>>>()))
      .Returns((int id, UpdateTransactionCategoryRequest _, int _, ICollection<Func<Task>> after) =>
      {
        if (failFor?.Invoke(id) == true)
          throw new InvalidOperationException("db hiccup");
        after.Add(() =>
        {
          ran.Add(id);
          return Task.CompletedTask;
        });
        return Task.FromResult(new TransactionDto());
      });

  [Fact]
  public async Task One_category_charges_run_their_side_effects_only_after_the_commit()
  {
    var scan = await OneCategoryTwoChargeScanAsync();
    var ran = new List<int>();
    DeferringRecategorizeQueues(ran);

    var result = await Service().ApplyAsync(1, scan.Id);

    result!.Status.Should().Be(ReceiptScanStatus.Applied);
    ran.Should().BeEquivalentTo(new[] { 100, 101 });
    // The plain call (which fires notifications, streaks and embeddings inline) is never used inside the transaction.
    _transactions.Verify(t => t.UpdateTransactionCategoryAsync(It.IsAny<int>(), It.IsAny<UpdateTransactionCategoryRequest>(), It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task A_rolled_back_apply_runs_no_side_effects()
  {
    var scan = await OneCategoryTwoChargeScanAsync();
    var ran = new List<int>();
    DeferringRecategorizeQueues(ran, failFor: id => id == 101);

    var result = await Service().ApplyAsync(1, scan.Id);

    result!.Status.Should().Be(ReceiptScanStatus.Ready);
    ran.Should().BeEmpty();
  }

  [Fact]
  public async Task A_retry_after_a_stop_part_way_converges()
  {
    var scan = await TwoChargeAmazonScanAsync();
    // The process dies after the first charge: an exception the service does not handle.
    SplitsWriteRows(failFor: id => id == 101 ? throw new OperationCanceledException("process stopped") : false);
    var stopped = () => Service().ApplyAsync(1, scan.Id);
    await stopped.Should().ThrowAsync<OperationCanceledException>();

    (await SplitStateAsync()).Should().Be((0, 0));
    var stuck = await _db.ReceiptScans.SingleAsync(r => r.Id == scan.Id);
    stuck.Status.Should().Be(ReceiptScanStatus.Applying);
    // The claim goes stale (written directly: SaveChanges stamps UpdatedAt itself).
    var stale = DateTime.UtcNow.AddMinutes(-11);
    await _db.ReceiptScans.Where(r => r.Id == scan.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.UpdatedAt, stale));
    _db.ChangeTracker.Clear();

    SplitsWriteRows();
    var retried = await Service().ApplyAsync(1, scan.Id);

    retried!.Status.Should().Be(ReceiptScanStatus.Applied);
    (await SplitStateAsync()).Should().Be((2, 4));
  }

  [Fact]
  public async Task A_single_charge_receipt_splits_and_applies_as_before()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m);
    ParserReturns(WalmartReceipt());
    var scan = await Service().ScanAsync(1, Shot(), null);
    Editable(100, 60m);
    SplitsWriteRows();

    var applied = await Service().ApplyAsync(1, scan.Id);

    applied!.Status.Should().Be(ReceiptScanStatus.Applied);
    applied.AppliedAt.Should().NotBeNull();
    (await SplitStateAsync()).Should().Be((1, 2));
  }

  [Fact]
  public async Task A_second_apply_while_one_is_running_does_nothing()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m);
    ParserReturns(WalmartReceipt());
    var scan = await Service().ScanAsync(1, Shot(), null);
    var row = await _db.ReceiptScans.SingleAsync(r => r.Id == scan.Id);
    row.Status = ReceiptScanStatus.Applying; // another Apply has claimed it
    await _db.SaveChangesAsync();

    (await Service().ApplyAsync(1, scan.Id)).Should().BeNull();
    _transactions.Verify(t => t.SplitTransactionAsync(It.IsAny<int>(), It.IsAny<SplitTransactionRequest>(), It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task A_charge_earmarked_to_a_savings_goal_is_never_matched()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m);
    var tx = await _db.Transactions.SingleAsync(t => t.Id == 100);
    tx.SavingsGoalId = 5;
    await _db.SaveChangesAsync();
    ParserReturns(WalmartReceipt());
    (await Service().ScanAsync(1, Shot(), null)).Status.Should().Be(ReceiptScanStatus.Unmatched);
  }

  [Fact]
  public async Task Dismissing_a_ready_receipt_closes_it_and_only_for_its_owner()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m);
    ParserReturns(WalmartReceipt());
    var scan = await Service().ScanAsync(1, Shot(), null);
    (await Service().DismissAsync(2, scan.Id)).Should().BeNull();
    (await Service().DismissAsync(1, scan.Id))!.Status.Should().Be(ReceiptScanStatus.Dismissed);
  }

  [Fact]
  public async Task The_receipt_for_a_transaction_is_found_by_any_of_its_charges()
  {
    await SeedUserAsync();
    await SeedChargeAsync(100, 60m);
    ParserReturns(WalmartReceipt());
    var scan = await Service().ScanAsync(1, Shot(), null);
    (await Service().GetScanForTransactionAsync(1, 100))!.Id.Should().Be(scan.Id);
    (await Service().GetScanForTransactionAsync(1, 999)).Should().BeNull();
  }
}

public class ReceiptParserTests
{
  private static readonly HashSet<int> Allowed = new() { 20, 24, 1001 };

  [Fact]
  public void A_fenced_reply_is_read_and_unknown_categories_are_dropped()
  {
    var reply =
      "```json\n{\"isReceipt\":true,\"retailer\":\"walmart\",\"orderNumber\":\"2000123\",\"orderDate\":\"2026-10-01\","
      + "\"kind\":\"delivered\",\"items\":[{\"name\":\" Milk \",\"quantity\":0,\"price\":\"4.50\",\"categoryId\":20},"
      + "{\"name\":\"Dog food\",\"quantity\":1,\"price\":26,\"categoryId\":77},{\"name\":\"\",\"price\":1}],"
      + "\"tax\":1.2,\"total\":31.70,\"charges\":[0,31.70]}\n```";
    var r = ReceiptParser.Parse(reply, Allowed)!;
    r.IsReceipt.Should().BeTrue();
    r.OrderDate.Should().Be(new DateTime(2026, 10, 1));
    r.Items.Should().HaveCount(2);
    r.Items[0].Name.Should().Be("Milk");
    r.Items[0].Quantity.Should().Be(1);
    r.Items[1].CategoryId.Should().BeNull();
    r.Charges.Should().Equal(31.70m);
    r.AddsUp().Should().BeTrue();
  }

  [Fact]
  public void Junk_replies_are_null()
  {
    ReceiptParser.Parse("I can't read that", Allowed).Should().BeNull();
    ReceiptParser.Parse("{not json}", Allowed).Should().BeNull();
    ReceiptParser.Parse(null, Allowed).Should().BeNull();
  }

  [Fact]
  public void The_prompt_lists_the_users_categories_and_how_to_treat_partial_screenshots()
  {
    var prompt = ReceiptParser.BuildPrompt(2, new DateTime(2026, 10, 2), new Dictionary<int, string> { [20] = "Groceries", [1001] = "Pets" });
    prompt.Should().Contain("1001: Pets").And.Contain("There are 2 file(s)").And.Contain("Never estimate or invent")
      .And.Contain("only part of a long order").And.Contain("Ignore anything else on screen")
      .And.Contain("Count each item once").And.Contain("multipleOrders true");
    ReceiptParser.BuildPrompt(1, new DateTime(2026, 10, 2), new Dictionary<int, string> { [20] = "Groceries" })
      .Should().NotContain("Count each item once");
  }
}
