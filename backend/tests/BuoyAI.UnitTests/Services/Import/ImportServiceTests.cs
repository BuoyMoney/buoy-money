using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs.Import;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services.Import;

public class ImportServiceTests
{
  private const string Simple =
    "Date,Description,Amount\n" +
    "2026-01-02,Paycheck,1500.00\n" +
    "2026-01-03,Groceries,-45.20\n" +
    "2026-01-04,Coffee,-4.50\n";

  private const string Ofx = @"OFXHEADER:100
DATA:OFXSGML
VERSION:102

<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS>
<BANKACCTFROM><BANKID>1<ACCTID>555<ACCTTYPE>CHECKING</BANKACCTFROM>
<BANKTRANLIST>
<STMTTRN><DTPOSTED>20260102<TRNAMT>-10.00<FITID>1<NAME>Alpha</STMTTRN>
<STMTTRN><DTPOSTED>20260103<TRNAMT>-20.00<FITID>2<NAME>Beta</STMTTRN>
<STMTTRN><DTPOSTED>20260104<TRNAMT>-30.00<FITID>3<NAME>Gamma</STMTTRN>
</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";

  private static ImportFileInput OfxFile(string name = "stmt.ofx") =>
    new() { FileName = name, Content = Encoding.ASCII.GetBytes(Ofx) };

  // ---- Preview and commit basics ----

  [Fact]
  public async Task Preview_ClassifiesRows_AndWritesNothing()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");

    var preview = await h.PreviewCsvAsync(1, Simple, account.Id);

    preview.Summary.Total.Should().Be(3);
    preview.Summary.New.Should().Be(3);
    preview.Summary.TotalInflow.Should().Be(1500m);
    preview.Summary.TotalOutflow.Should().Be(49.70m);
    preview.Summary.FirstDate.Should().Be(new DateOnly(2026, 1, 2));
    preview.Accounts.Should().ContainSingle().Which.MatchedAccountId.Should().Be(account.Id);
    preview.Rows.Should().HaveCount(3);
    preview.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    (await h.Db.Transactions.CountAsync()).Should().Be(0);
  }

  [Fact]
  public async Task Commit_WritesThroughIngest_WithScopedIdsBatchAndReviewFlags()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    var csv = "Date,Description,Amount,Memo\n2026-01-03,Groceries,-45.20,weekly shop\n";

    var preview = await h.PreviewCsvAsync(1, csv, account.Id);
    var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());

    result.Imported.Should().Be(1);
    result.BatchId.Should().NotBeNullOrEmpty();
    var tx = await h.Db.Transactions.SingleAsync();
    tx.PlaidTransactionId.Should().StartWith($"import:csv:{account.Id}:");
    tx.PlaidTransactionId.Length.Should().BeLessThanOrEqualTo(100);
    tx.AccountId.Should().Be(account.Id);
    tx.Amount.Should().Be(-45.20m);
    tx.Type.Should().Be(TransactionType.Debit);
    tx.Description.Should().Be("Groceries");
    tx.UserNotes.Should().Be("weekly shop");
    tx.ImportBatchId.Should().Be(result.BatchId);
    tx.NeedsReview.Should().BeTrue();
    tx.IsUserCategorized.Should().BeFalse();
    tx.CategorySource.Should().Be("Import");
    tx.Date.Should().Be(new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));

    // The ingest categorizer saw the new row, so rules and the AI queue apply to imports too.
    h.Categorizer.Verify(c => c.ProcessIngestedAsync(
      It.Is<IReadOnlyCollection<Transaction>>(t => t.Count == 1), It.IsAny<System.Threading.CancellationToken>()), Times.Once);
  }

  // ---- The id-collision bug ----

  // Before: the raw FITID went into a column that is unique across ALL accounts, so the same bank
  // id arriving in a second account made the whole second file fail.
  [Fact]
  public async Task SameFitIdInTwoAccounts_BothFilesImport()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var a = h.AddAccount(1, "Checking", "acct-1");
    var b = h.AddAccount(1, "Other bank", "acct-2");

    foreach (var account in new[] { a, b })
    {
      var preview = await h.Service.PreviewAsync(1, new[] { OfxFile() }, new ImportPreviewRequest { AccountId = account.Id });
      var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());
      result.Imported.Should().Be(3);
    }

    (await h.Db.Transactions.CountAsync(t => t.AccountId == a.Id)).Should().Be(3);
    (await h.Db.Transactions.CountAsync(t => t.AccountId == b.Id)).Should().Be(3);
    (await h.Db.Transactions.Select(t => t.PlaidTransactionId).Distinct().CountAsync()).Should().Be(6);
  }

  [Fact]
  public void ExternalId_IsCappedAtTheColumnWidth_AndStaysDistinct()
  {
    var longId = new string('x', 300);
    var one = new ParsedRowFactory().Row(longId + "1");
    var two = new ParsedRowFactory().Row(longId + "2");

    var idOne = BuoyAI.Infrastructure.Services.Import.ImportPlanner.BuildExternalId(ImportSource.Ofx, 5, one);
    var idTwo = BuoyAI.Infrastructure.Services.Import.ImportPlanner.BuildExternalId(ImportSource.Ofx, 5, two);

    idOne.Length.Should().BeLessThanOrEqualTo(100);
    idOne.Should().NotBe(idTwo);
    idOne.Should().StartWith("import:ofx:5:");
  }

  // ---- Duplicates ----

  [Fact]
  public async Task Reimporting_TheSameFile_IsIdempotent()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");

    var first = await h.PreviewCsvAsync(1, Simple, account.Id);
    await h.Service.CommitAsync(1, first.ImportId, new ImportCommitRequest());

    var second = await h.PreviewCsvAsync(1, Simple, account.Id);
    second.Summary.DuplicateExact.Should().Be(3);
    second.Summary.New.Should().Be(0);

    var result = await h.Service.CommitAsync(1, second.ImportId, new ImportCommitRequest());
    result.Imported.Should().Be(0);
    result.SkippedDuplicates.Should().Be(3);
    (await h.Db.Transactions.CountAsync()).Should().Be(3);
  }

  [Fact]
  public async Task RepeatedFitIdInOneFile_IsClassifiedAsDuplicate_AndTheImportSucceeds()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    var ofx = Ofx.Replace("<FITID>2<NAME>Beta", "<FITID>1<NAME>Beta");
    var file = new ImportFileInput { FileName = "dup.ofx", Content = Encoding.ASCII.GetBytes(ofx) };

    var preview = await h.Service.PreviewAsync(1, new[] { file }, new ImportPreviewRequest { AccountId = account.Id });
    preview.Summary.DuplicateExact.Should().Be(1);
    preview.Summary.New.Should().Be(2);

    var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());
    result.Imported.Should().Be(2);
  }

  [Fact]
  public async Task CreatingAnAccount_AsAReadOnlyFamilyMember_IsRejected()
  {
    using var h = new ImportHarness();
    h.Family.Setup(f => f.CanModifyFinancialDataAsync(1)).ReturnsAsync(false);
    h.AddUser(1, familyId: 5);

    var preview = await h.PreviewCsvAsync(1, Simple);
    var act = () => h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());

    (await act.Should().ThrowAsync<ImportException>()).Which.StatusCode.Should().Be(403);
    (await h.Db.Accounts.CountAsync()).Should().Be(0);
  }

  [Fact]
  public async Task TwoIdenticalPurchasesOnOneDay_BothImport_AndReimportSkipsBoth()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    const string csv = "Date,Description,Amount\n2026-01-02,Coffee,-4.50\n2026-01-02,Coffee,-4.50\n";

    var preview = await h.PreviewCsvAsync(1, csv, account.Id);
    preview.Summary.New.Should().Be(2);
    (await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest())).Imported.Should().Be(2);

    var again = await h.PreviewCsvAsync(1, csv, account.Id);
    again.Summary.DuplicateExact.Should().Be(2);
  }

  [Fact]
  public async Task BankSyncedRow_WithSimilarPayeeAndNearbyDate_IsALikelyDuplicate_SkippedByDefault()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    var plaid = h.AddTransaction(account.Id, "plaid-abc", -4.50m, new DateTime(2026, 1, 2), "STARBUCKS #1234", "Starbucks");
    const string csv = "Date,Description,Amount\n2026-01-04,POS PURCHASE STARBUCKS,-4.50\n";

    var preview = await h.PreviewCsvAsync(1, csv, account.Id);

    preview.Summary.DuplicateLikely.Should().Be(1);
    preview.Rows[0].Class.Should().Be(ImportRowClass.DuplicateLikely);
    preview.Rows[0].MatchedTransactionId.Should().Be(plaid.Id);

    var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());
    result.Imported.Should().Be(0);
    result.SkippedDuplicates.Should().Be(1);
  }

  [Fact]
  public async Task LikelyDuplicate_CanBeImportedByRowOverride()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    h.AddTransaction(account.Id, "plaid-abc", -4.50m, new DateTime(2026, 1, 2), "Starbucks");
    var preview = await h.PreviewCsvAsync(1, "Date,Description,Amount\n2026-01-02,Starbucks,-4.50\n", account.Id);

    var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest
    {
      Overrides = new Dictionary<int, string> { [preview.Rows[0].RowId] = "import" }
    });

    result.Imported.Should().Be(1);
  }

  [Fact]
  public async Task SameAmountSameDay_DifferentPayee_IsOnlyPossible_AndImportedByDefault()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    h.AddTransaction(account.Id, "plaid-abc", -25.00m, new DateTime(2026, 1, 2), "AMZN Mktp US*2K4", "Amazon");
    var preview = await h.PreviewCsvAsync(1, "Date,Description,Amount\n2026-01-02,Hardware Store,-25.00\n", account.Id);

    preview.Rows[0].Class.Should().Be(ImportRowClass.PossibleDuplicate);
    (await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest())).Imported.Should().Be(1);
  }

  [Fact]
  public async Task SameAmount_FarApart_IsNew()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    h.AddTransaction(account.Id, "plaid-abc", -25.00m, new DateTime(2026, 1, 2), "Hardware Store");
    var preview = await h.PreviewCsvAsync(1, "Date,Description,Amount\n2026-01-20,Hardware Store,-25.00\n", account.Id);
    preview.Rows[0].Class.Should().Be(ImportRowClass.New);
  }

  [Fact]
  public async Task ManualEntry_MatchingTheFile_IsALikelyDuplicate()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    h.AddTransaction(account.Id, "manual_5f1c", -12.00m, new DateTime(2026, 1, 5), "Lunch at Joes");
    var preview = await h.PreviewCsvAsync(1, "Date,Description,Amount\n2026-01-05,LUNCH AT JOES,-12.00\n", account.Id);
    preview.Rows[0].Class.Should().Be(ImportRowClass.DuplicateLikely);
  }

  [Fact]
  public async Task OneStoredRow_IsNotBlamedForTwoFileRows()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    h.AddTransaction(account.Id, "plaid-abc", -4.50m, new DateTime(2026, 1, 2), "Coffee");
    var preview = await h.PreviewCsvAsync(1, "Date,Description,Amount\n2026-01-02,Coffee,-4.50\n2026-01-02,Coffee,-4.50\n", account.Id);

    preview.Rows.Count(r => r.Class == ImportRowClass.DuplicateLikely).Should().Be(1);
    preview.Rows.Count(r => r.Class == ImportRowClass.New).Should().Be(1);
  }

  // ---- Permissions ----

  [Fact]
  public async Task AnotherUsersAccount_ReadsAsNotFound()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    h.AddUser(2);
    var theirs = h.AddAccount(2, "Theirs", "acct-2");

    var act = () => h.PreviewCsvAsync(1, Simple, theirs.Id);
    (await act.Should().ThrowAsync<ImportException>()).Which.StatusCode.Should().Be(404);
  }

  [Fact]
  public async Task ReadOnlyFamilyMember_IsRefused()
  {
    using var h = new ImportHarness();
    h.AddUser(1, familyId: 10);
    h.AddUser(2, familyId: 10);
    var shared = h.AddAccount(2, "Family card", "acct-f", familyId: 10);
    h.Family.Setup(f => f.CanModifyFinancialDataAsync(1)).ReturnsAsync(false);

    var act = () => h.PreviewCsvAsync(1, Simple, shared.Id);
    (await act.Should().ThrowAsync<ImportException>()).Which.StatusCode.Should().Be(403);
  }

  [Fact]
  public async Task ReadOnlyMember_IsRefusedOnAFamilyAccountTheyOwn()
  {
    using var h = new ImportHarness();
    h.AddUser(1, familyId: 10);
    var mine = h.AddAccount(1, "My family card", "acct-m", familyId: 10);
    h.Family.Setup(f => f.CanModifyFinancialDataAsync(1)).ReturnsAsync(false);

    var act = () => h.PreviewCsvAsync(1, Simple, mine.Id);
    (await act.Should().ThrowAsync<ImportException>()).Which.StatusCode.Should().Be(403);
  }

  [Fact]
  public async Task BudgetAdminOrMember_CanImportIntoAFamilyAccount()
  {
    using var h = new ImportHarness();
    h.AddUser(1, familyId: 10);
    h.AddUser(2, familyId: 10);
    var shared = h.AddAccount(2, "Family card", "acct-f", familyId: 10);

    var preview = await h.PreviewCsvAsync(1, Simple, shared.Id);
    var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());

    result.Imported.Should().Be(3);
    (await h.Db.Transactions.CountAsync(t => t.AccountId == shared.Id)).Should().Be(3);
  }

  [Fact]
  public async Task AFamilyAccountInAnotherFamily_IsNotReachable()
  {
    using var h = new ImportHarness();
    h.AddUser(1, familyId: 10);
    h.AddUser(2, familyId: 20);
    var other = h.AddAccount(2, "Other family", "acct-o", familyId: 20);

    var act = () => h.PreviewCsvAsync(1, Simple, other.Id);
    (await act.Should().ThrowAsync<ImportException>()).Which.StatusCode.Should().Be(404);
  }

  [Fact]
  public async Task AStagedImport_BelongsToItsUser()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    h.AddUser(2);
    var account = h.AddAccount(1, "Checking", "acct-1");
    var preview = await h.PreviewCsvAsync(1, Simple, account.Id);

    var act = () => h.Service.CommitAsync(2, preview.ImportId, new ImportCommitRequest());
    (await act.Should().ThrowAsync<ImportException>()).Which.StatusCode.Should().Be(404);
  }

  [Fact]
  public async Task CommitTwice_SecondTimeIsExpired_NotDuplicated()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    var preview = await h.PreviewCsvAsync(1, Simple, account.Id);
    await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());

    var act = () => h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());
    await act.Should().ThrowAsync<ImportException>();
    (await h.Db.Transactions.CountAsync()).Should().Be(3);
  }

  // ---- Dates, categories, accounts ----

  [Fact]
  public async Task AmbiguousDates_BlockCommitUntilTheUserPicksAnOrder()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    const string csv = "Date,Description,Amount\n03/04/2026,A,-1.00\n05/06/2026,B,-2.00\n";

    var preview = await h.PreviewCsvAsync(1, csv, account.Id);
    preview.NeedsDateOrderChoice.Should().BeTrue();
    var blocked = () => h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());
    await blocked.Should().ThrowAsync<ImportException>().WithMessage("*two ways*");

    var chosen = await h.Service.ReparseAsync(1, preview.ImportId, new ImportPreviewRequest
    {
      AccountId = account.Id,
      Mapping = new ImportMapping { DateOrder = ImportDateOrder.DayFirst }
    });
    chosen.NeedsDateOrderChoice.Should().BeFalse();
    await h.Service.CommitAsync(1, chosen.ImportId, new ImportCommitRequest());

    (await h.Db.Transactions.OrderBy(t => t.Date).FirstAsync()).Date.Should().Be(new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc));
  }

  [Fact]
  public async Task WithoutATargetAccount_AFileProposesCreatingOne_AndCommitCreatesIt()
  {
    using var h = new ImportHarness();
    h.AddUser(1);

    var preview = await h.PreviewCsvAsync(1, Simple, fileName: "My Bank.csv");
    preview.Accounts.Should().ContainSingle();
    preview.Accounts[0].ProposedAction.Should().Be("create");
    preview.Accounts[0].SourceName.Should().Be("My Bank");

    var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());

    result.AccountsCreated.Should().Be(1);
    result.Imported.Should().Be(3);
    var account = await h.Db.Accounts.SingleAsync();
    account.Name.Should().Be("My Bank");
    account.UserId.Should().Be(1);
    account.PlaidAccountId.Should().StartWith("import-");
    account.PlaidConnectionId.Should().BeNull();
  }

  [Fact]
  public async Task ALongAccountNameEndingInEmoji_IsCutWithoutSplittingASurrogatePair()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var preview = await h.PreviewCsvAsync(1, Simple, fileName: "Bank.csv");

    await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest
    {
      AccountMappings = new List<ImportAccountMapping>
      {
        new() { SourceKey = preview.Accounts[0].SourceKey, Action = "create", Name = new string('a', 99) + "😀" }
      }
    });

    var name = (await h.Db.Accounts.SingleAsync()).Name;
    name.Should().Be(new string('a', 99));
    char.IsHighSurrogate(name[^1]).Should().BeFalse();
  }

  [Fact]
  public async Task FileCategories_MapToExistingOrNewCategories_AndAreMarkedUserChosen()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    const string csv =
      "Date,Description,Amount,Category\n" +
      "2026-01-02,Grocery Mart,-45.20,Groceries\n" +
      "2026-01-03,Pottery Class,-30.00,Hobbies\n" +
      "2026-01-04,Mystery,-5.00,\n";

    var preview = await h.PreviewCsvAsync(1, csv, account.Id);
    preview.Categories.Should().HaveCount(2);
    preview.Categories.First(c => c.SourceName == "Groceries").ProposedCategoryId.Should().Be(20);
    preview.Categories.First(c => c.SourceName == "Hobbies").ProposedCategoryId.Should().BeNull();

    var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());

    result.CategoriesCreated.Should().Be(1);
    h.CreatedCategories.Should().ContainSingle().Which.Name.Should().Be("Hobbies");

    var rows = await h.Db.Transactions.OrderBy(t => t.Date).ToListAsync();
    rows[0].CategoryId.Should().Be(20);
    rows[0].IsUserCategorized.Should().BeTrue();
    rows[0].NeedsReview.Should().BeFalse();
    rows[1].CategoryId.Should().Be(h.CreatedCategories[0].Id);
    rows[2].CategoryId.Should().Be(SystemCategoryIds.Uncategorized);
    rows[2].NeedsReview.Should().BeTrue();
  }

  [Fact]
  public async Task CategoryMapping_LetsTheUserRedirectOrDropACategory()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    const string csv =
      "Date,Description,Amount,Category\n" +
      "2026-01-02,Grocery Mart,-45.20,Groceries\n" +
      "2026-01-03,Pottery Class,-30.00,Hobbies\n";
    var preview = await h.PreviewCsvAsync(1, csv, account.Id);

    await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest
    {
      CategoryMappings = new List<ImportCategoryMapping>
      {
        new() { SourceName = "Groceries", Action = "map", CategoryId = 21 },
        new() { SourceName = "Hobbies", Action = "none" }
      }
    });

    var rows = await h.Db.Transactions.OrderBy(t => t.Date).ToListAsync();
    rows[0].CategoryId.Should().Be(21);
    rows[1].CategoryId.Should().Be(SystemCategoryIds.Uncategorized);
    h.CreatedCategories.Should().BeEmpty();
  }

  [Fact]
  public async Task IncludeClassesAndAccountSkip_LimitWhatIsWritten()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    const string csv =
      "Date,Account,Description,Amount\n" +
      "2026-01-02,Checking,A,-1.00\n" +
      "2026-01-03,Savings,B,-2.00\n";
    var preview = await h.PreviewCsvAsync(1, csv);
    preview.Accounts.Select(a => a.SourceName).Should().BeEquivalentTo("Checking", "Savings");

    var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest
    {
      AccountMappings = new List<ImportAccountMapping>
      {
        new() { SourceKey = "Savings", Action = "skip" }
      }
    });

    result.AccountsCreated.Should().Be(1);
    result.Imported.Should().Be(1);
    (await h.Db.Accounts.SingleAsync()).Name.Should().Be("Checking");
  }

  [Fact]
  public async Task Rows_AreServedInPagesAndFilteredByClass()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var account = h.AddAccount(1, "Checking", "acct-1");
    h.AddTransaction(account.Id, "plaid-1", -1.00m, new DateTime(2026, 1, 2), "Alpha");
    var preview = await h.PreviewCsvAsync(1, "Date,Description,Amount\n2026-01-02,Alpha,-1.00\n2026-01-03,Beta,-2.00\n2026-01-04,Gamma,-3.00\n", account.Id);

    var all = await h.Service.GetRowsAsync(1, preview.ImportId, null, 0, 2);
    all.Total.Should().Be(3);
    all.Rows.Should().HaveCount(2);

    var likely = await h.Service.GetRowsAsync(1, preview.ImportId, ImportRowClass.DuplicateLikely, 0, 50);
    likely.Rows.Should().ContainSingle().Which.Payee.Should().Be("Alpha");
  }

  // ---- Undo ----

  [Fact]
  public async Task Undo_RemovesTheBatch_AndAccountsItCreated()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var existing = h.AddAccount(1, "Existing", "acct-1");
    h.AddTransaction(existing.Id, "plaid-keep", -1.00m, new DateTime(2025, 12, 1), "Keep me");

    var preview = await h.PreviewCsvAsync(1, Simple, fileName: "New.csv");
    var committed = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());
    (await h.Db.Transactions.CountAsync()).Should().Be(4);

    var undone = await h.Service.UndoAsync(1, committed.BatchId);

    undone.TransactionsRemoved.Should().Be(3);
    undone.AccountsRemoved.Should().Be(1);
    (await h.Db.Transactions.SingleAsync()).PlaidTransactionId.Should().Be("plaid-keep");
    (await h.Db.Accounts.SingleAsync()).Name.Should().Be("Existing");
  }

  [Fact]
  public async Task Undo_IsRefusedForAnotherUser_AndAfterADay()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    h.AddUser(2);
    var account = h.AddAccount(1, "Checking", "acct-1");
    var preview = await h.PreviewCsvAsync(1, Simple, account.Id);
    var committed = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());

    var stranger = () => h.Service.UndoAsync(2, committed.BatchId);
    (await stranger.Should().ThrowAsync<ImportException>()).Which.StatusCode.Should().Be(404);

    foreach (var tx in h.Db.Transactions) tx.CreatedAt = DateTime.UtcNow.AddDays(-2);
    await h.Db.SaveChangesAsync();
    var late = () => h.Service.UndoAsync(1, committed.BatchId);
    await late.Should().ThrowAsync<ImportException>().WithMessage("*more than a day*");
  }

  // ---- Limits ----

  [Fact]
  public async Task Previews_AreRateLimitedPerUser()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    h.AddUser(2);
    for (var i = 0; i < ImportServiceLimits.PreviewsPerHour; i++)
      await h.PreviewCsvAsync(1, Simple);

    var act = () => h.PreviewCsvAsync(1, Simple);
    (await act.Should().ThrowAsync<ImportException>()).Which.StatusCode.Should().Be(429);

    await h.PreviewCsvAsync(2, Simple); // another user is unaffected
  }

  [Fact]
  public async Task UnsupportedFileTypes_AreRefused()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var act = () => h.Service.PreviewAsync(1, new[] { ImportHarness.Csv("notes.exe", "hello") }, new ImportPreviewRequest());
    await act.Should().ThrowAsync<ImportException>().WithMessage("*not a supported file type*");
  }

  // ---- Other formats end to end ----

  [Fact]
  public async Task Ofx_WithTwoStatements_ProposesOneAccountEach()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var twoStatements = Ofx.Replace("</STMTRS></STMTTRNRS>",
      "</STMTRS></STMTTRNRS><STMTTRNRS><STMTRS><BANKACCTFROM><BANKID>1<ACCTID>777<ACCTTYPE>SAVINGS</BANKACCTFROM><BANKTRANLIST>" +
      "<STMTTRN><DTPOSTED>20260105<TRNAMT>-5.00<FITID>1<NAME>Delta</STMTTRN></BANKTRANLIST></STMTRS></STMTTRNRS>");
    var preview = await h.Service.PreviewAsync(1,
      new[] { new ImportFileInput { FileName = "two.qfx", Content = Encoding.ASCII.GetBytes(twoStatements) } },
      new ImportPreviewRequest());

    preview.Accounts.Should().HaveCount(2);
    var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest());

    result.Imported.Should().Be(4);
    result.AccountsCreated.Should().Be(2);
    // Both statements use FITID 1: the scoped ids keep them apart.
    (await h.Db.Transactions.Select(t => t.PlaidTransactionId).Distinct().CountAsync()).Should().Be(4);
  }

  [Fact]
  public async Task Ynab_CreatesAccountsAndCategories_FilesTransfersAndBuildsBalances_ThenUndoes()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var files = new[]
    {
      ImportHarness.Csv("Plan - Register.csv", YnabImportParserTests.Register),
      ImportHarness.Csv("Plan - Plan.csv", YnabImportParserTests.Plan)
    };

    var preview = await h.Service.PreviewAsync(1, files, new ImportPreviewRequest());
    preview.Detected.Source.Should().Be("ynab");
    preview.Accounts.Select(a => a.SourceName).Should().BeEquivalentTo("Checking", "Savings", "Visa Card");
    preview.Budget.Should().NotBeNull();
    preview.Budget!.LatestMonth.Should().Be("2026-01");
    preview.Budget.LatestMonthTotal.Should().Be(500m);
    preview.Categories.First(c => c.SourceName == "Groceries").ProposedCategoryId.Should().Be(20);
    preview.Categories.First(c => c.SourceName == "Ready to Assign").ProposedCategoryId.Should().Be(4);

    var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest { CreateBudget = true });

    result.AccountsCreated.Should().Be(3);
    result.Imported.Should().Be(7);
    result.CategoriesCreated.Should().Be(1); // "Supplies"
    result.BudgetCreated.Should().BeTrue();

    var rows = await h.Db.Transactions.Include(t => t.Account).ToListAsync();
    rows.Where(r => r.Description == "Transfer : Savings" || r.Description == "Transfer : Checking")
      .Should().OnlyContain(r => r.CategoryId == SystemCategoryIds.Transfer && r.IsUserCategorized);

    var checking = await h.Db.Accounts.FirstAsync(a => a.Name == "Checking");
    checking.CurrentBalance.Should().Be(2000m - 45.20m - 300m);
    var visa = await h.Db.Accounts.FirstAsync(a => a.Name == "Visa Card");
    visa.Type.Should().Be(AccountType.CreditCard);
    visa.CurrentBalance.Should().Be(0m); // $50 of charges, then a $50 payment: nothing owed

    var request = h.BudgetRequests.Should().ContainSingle().Subject;
    request.Period.Should().Be(BudgetPeriod.Monthly);
    request.Categories.Should().HaveCount(2);
    request.Categories.First(c => c.CategoryId == 20).AllocatedAmount.Should().Be(400m);

    var undone = await h.Service.UndoAsync(1, result.BatchId);
    undone.TransactionsRemoved.Should().Be(7);
    undone.AccountsRemoved.Should().Be(3);
    (await h.Db.Transactions.CountAsync()).Should().Be(0);
  }

  [Fact]
  public async Task Actual_ImportsAccountsHistoryAndBudget()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    var preview = await h.Service.PreviewAsync(1,
      new[] { new ImportFileInput { FileName = "My-Finances.zip", Content = ActualFixture.Build() } },
      new ImportPreviewRequest());

    preview.Detected.Source.Should().Be("actual");
    preview.Accounts.Should().HaveCount(4);
    preview.Accounts.First(a => a.SourceName == "Old Card").Closed.Should().BeTrue();

    var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest { CreateBudget = true });

    result.Imported.Should().Be(7);
    result.BudgetCreated.Should().BeTrue();
    (await h.Db.Accounts.FirstAsync(a => a.Name == "Old Card")).IsActive.Should().BeFalse();
    (await h.Db.Transactions.CountAsync(t => t.PlaidTransactionId.StartsWith("import:actual:"))).Should().Be(7);

    // Re-importing the same export adds nothing, because Actual's own ids are the identity.
    var again = await h.Service.PreviewAsync(1,
      new[] { new ImportFileInput { FileName = "My-Finances.zip", Content = ActualFixture.Build() } },
      new ImportPreviewRequest());
    again.Accounts.Should().OnlyContain(a => a.ProposedAction == "match");
    again.Summary.DuplicateExact.Should().Be(7);
    (await h.Service.CommitAsync(1, again.ImportId, new ImportCommitRequest())).Imported.Should().Be(0);
  }

  [Fact]
  public async Task Budget_IsNotCreatedWhenTheUserAlreadyHasOne()
  {
    using var h = new ImportHarness();
    h.AddUser(1);
    h.Db.Budgets.Add(new Budget { UserId = 1, Name = "Mine", IsActive = true, Type = BudgetType.Personal });
    await h.Db.SaveChangesAsync();

    var preview = await h.Service.PreviewAsync(1,
      new[] { ImportHarness.Csv("r.csv", YnabImportParserTests.Register), ImportHarness.Csv("p.csv", YnabImportParserTests.Plan) },
      new ImportPreviewRequest());
    var result = await h.Service.CommitAsync(1, preview.ImportId, new ImportCommitRequest { CreateBudget = true });

    result.BudgetCreated.Should().BeFalse();
    result.Warnings.Should().Contain(w => w.Contains("already have an active budget"));
    h.Budgets.Verify(b => b.CreateBudgetAsync(It.IsAny<int>(), It.IsAny<BuoyAI.Core.DTOs.Budget.CreateBudgetRequest>()), Times.Never);
  }

  // ---- The original endpoint ----

  [Fact]
  public async Task LegacyEndpoint_StillImportsDebitCreditCsv_AndRefusesAForeignAccount()
  {
    using var h = new ImportHarness();
    h.AddUser(7);
    h.AddUser(8);
    var mine = h.AddAccount(7, "Checking", "acct-1");
    var theirs = h.AddAccount(8, "Theirs", "acct-2");
    var legacy = new BuoyAI.Infrastructure.Services.TransactionImportService(
      h.Service, Microsoft.Extensions.Logging.Abstractions.NullLogger<BuoyAI.Infrastructure.Services.TransactionImportService>.Instance);

    const string csv =
      "Date,Description,Debit,Credit\n" +
      "2026-01-02,Paycheck,,1500.00\n" +
      "2026-01-03,Groceries,45.20,\n" +
      "2026-01-04,Refund,,12.34\n";

    using (var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(csv)))
    {
      var ok = await legacy.ImportFromFileAsync(7, mine.Id, stream, "bank.csv");
      ok.Success.Should().BeTrue();
      ok.ImportedCount.Should().Be(3);
      ok.SkippedInvalid.Should().Be(0);
    }
    (await h.Db.Transactions.OrderBy(t => t.Date).Select(t => t.Amount).ToListAsync())
      .Should().Equal(1500.00m, -45.20m, 12.34m);

    using var stream2 = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(csv));
    var denied = await legacy.ImportFromFileAsync(7, theirs.Id, stream2, "bank.csv");
    denied.Success.Should().BeFalse();
    denied.Errors.Should().ContainSingle().Which.Should().Contain("not found");
  }

  [Fact]
  public async Task LegacyEndpoint_ReadsAmbiguousDatesMonthFirst_AndSaysSo()
  {
    using var h = new ImportHarness();
    h.AddUser(7);
    var account = h.AddAccount(7, "Checking", "acct-1");
    var legacy = new BuoyAI.Infrastructure.Services.TransactionImportService(
      h.Service, Microsoft.Extensions.Logging.Abstractions.NullLogger<BuoyAI.Infrastructure.Services.TransactionImportService>.Instance);

    using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes("Date,Description,Amount\n03/04/2026,A,-1.00\n"));
    var result = await legacy.ImportFromFileAsync(7, account.Id, stream, "bank.csv");

    result.Success.Should().BeTrue();
    result.Warnings.Should().Contain(w => w.Contains("month first"));
    (await h.Db.Transactions.SingleAsync()).Date.Month.Should().Be(3);
  }

  private sealed class ParsedRowFactory
  {
    public BuoyAI.Infrastructure.Services.Import.ParsedImportRow Row(string sourceId) => new()
    {
      RowId = 0,
      Date = new DateOnly(2026, 1, 2),
      Amount = -1m,
      Payee = "x",
      SourceId = sourceId
    };
  }
}

/// <summary>Mirrors the service limits so tests do not hard-code them.</summary>
internal static class ImportServiceLimits
{
  public const int PreviewsPerHour = BuoyAI.Infrastructure.Services.ImportService.PreviewsPerHour;
}
