using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Core.Utilities;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class TransactionServiceTests
{
  private static TransactionService CreateService(
    Mock<ITransactionRepository> repositoryMock,
    Mock<IUserRepository>? userRepositoryMock = null,
    Mock<IBudgetRepository>? budgetRepositoryMock = null,
    Mock<ICategoryRepository>? categoryRepositoryMock = null,
    Mock<INotificationService>? notificationServiceMock = null,
    Mock<IFamilyAuthorizationService>? authServiceMock = null,
    Mock<ISubscriptionTransactionRepository>? subscriptionTransactionRepositoryMock = null,
    Mock<ILogger<TransactionService>>? loggerMock = null,
    Mock<IAccountRepository>? accountRepositoryMock = null,
    Mock<ISavingsService>? savingsServiceMock = null)
  {
    // Default: ExecuteInTransactionAsync just runs the action inline (no real DB transaction in unit tests).
    repositoryMock.Setup(r => r.ExecuteInTransactionAsync(It.IsAny<Func<Task>>()))
      .Returns<Func<Task>>(action => action());

    var subTransRepoMock = subscriptionTransactionRepositoryMock ?? new Mock<ISubscriptionTransactionRepository>();
    // Default setup: no transactions are recurring
    subTransRepoMock.Setup(r => r.GetRecurringTransactionIdsAsync(It.IsAny<IEnumerable<int>>()))
      .ReturnsAsync(new HashSet<int>());
    
    // Default category repository: returns system categories by their ID
    var catRepoMock = categoryRepositoryMock ?? new Mock<ICategoryRepository>();
    catRepoMock.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<int>>()))
      .ReturnsAsync((IEnumerable<int> ids) => 
        ids.Where(id => id < 1000).Select(id => new Category { Id = id, Name = ((TransactionCategory)id).GetDisplayName() }).ToList());
    
    return new TransactionService(
      repositoryMock.Object,
      (accountRepositoryMock ?? new Mock<IAccountRepository>()).Object,
      (userRepositoryMock ?? new Mock<IUserRepository>()).Object,
      (budgetRepositoryMock ?? new Mock<IBudgetRepository>()).Object,
      catRepoMock.Object,
      (notificationServiceMock ?? new Mock<INotificationService>()).Object,
      (authServiceMock ?? CreateDefaultAuthServiceMock()).Object,
      subTransRepoMock.Object,
      new Mock<ITransactionEmbeddingService>().Object,
      (loggerMock ?? new Mock<ILogger<TransactionService>>()).Object,
      Mock.Of<IGamificationEvaluationQueue>(),
      Mock.Of<IGamificationService>(),
      new Lazy<ISavingsService>(() => (savingsServiceMock ?? new Mock<ISavingsService>()).Object));
  }

  private static Mock<IFamilyAuthorizationService> CreateDefaultAuthServiceMock()
  {
    var mock = new Mock<IFamilyAuthorizationService>();
    // By default, allow all operations
    mock.Setup(s => s.CanAccessTransactionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ResourcePermission>()))
        .ReturnsAsync(AuthorizationResult.Allowed());
    mock.Setup(s => s.ValidateTransactionAccessOrThrowAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ResourcePermission>()))
        .Returns(Task.CompletedTask);
    mock.Setup(s => s.CanModifyFinancialDataAsync(It.IsAny<int>()))
        .ReturnsAsync(true);
    return mock;
  }

  private static Transaction CreateTransaction(
    int id,
    int accountId,
    decimal amount,
    TransactionCategory category,
    DateTime date,
    string description = "Sample",
    string merchant = "Merchant",
    int userId = 1,
    string accountName = "Account",
    string? accountNickname = null)
  {
    return new Transaction
    {
      Id = id,
      AccountId = accountId,
      Amount = amount,
      Type = amount >= 0 ? TransactionType.Credit : TransactionType.Debit,
      Description = description,
      MerchantName = merchant,
      CategoryId = (int)category,
      IsUserCategorized = false,
      Date = date,
      Account = new Account
      {
        Id = accountId,
        UserId = userId,
        Name = accountName,
        Nickname = accountNickname
      }
    };
  }

  [Fact]
  public async Task GetTransactionsAsync_ShouldApplyFiltersAndMapResults()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var filter = new TransactionFilterRequest
    {
      StartDate = new DateTime(2024, 1, 1),
      EndDate = new DateTime(2024, 1, 31),
      CategoryId = (int)TransactionCategory.Groceries,
      AccountId = 10,
      SearchTerm = "trader",
      MinAmount = 50,
      MaxAmount = 150,
      Page = 1,
      PageSize = 2
    };

    var transactions = new List<Transaction>
    {
      CreateTransaction(1, 10, -120, TransactionCategory.Groceries, new DateTime(2024, 1, 5), "Trader Joe's run", "Trader Joe's", accountName: "Daily Checking"),
      CreateTransaction(2, 10, -40, TransactionCategory.Groceries, new DateTime(2024, 1, 6), "Snack", "Local Store"),
      CreateTransaction(3, 11, -90, TransactionCategory.Groceries, new DateTime(2024, 1, 7), "Trader Sam's", "Trader Sam's"),
      CreateTransaction(4, 10, -130, TransactionCategory.Entertainment, new DateTime(2024, 1, 8), "Concert", "Live Nation")
    };

    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(42, null, filter.StartDate, filter.EndDate))
      .ReturnsAsync(transactions);

    var service = CreateService(repositoryMock);

    var result = (await service.GetTransactionsAsync(42, filter)).ToList();

    result.Should().HaveCount(1);
    var dto = result.Single();
    dto.Id.Should().Be(1);
    dto.CategoryId.Should().Be((int)TransactionCategory.Groceries);
    dto.CategoryName.Should().Be("Groceries");
  dto.AccountName.Should().Be("Daily Checking");
    dto.IsRecurring.Should().BeFalse();
  }

  // Offline-sync delta fetch (#493): updatedAfter returns only transactions modified after the
  // client's last-seen timestamp, and the DTO carries UpdatedAt so the client can advance its cursor.
  [Fact]
  public async Task GetTransactionsAsync_UpdatedAfterFilter_ReturnsOnlyNewerTransactionsWithUpdatedAt()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var cutoff = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    var filter = new TransactionFilterRequest { Page = 1, PageSize = 10, UpdatedAfter = cutoff };

    var stale = CreateTransaction(1, 10, -20, TransactionCategory.Groceries, new DateTime(2024, 1, 5));
    stale.UpdatedAt = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc); // before cutoff
    var fresh = CreateTransaction(2, 10, -30, TransactionCategory.Groceries, new DateTime(2024, 1, 6));
    fresh.UpdatedAt = new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Utc); // after cutoff

    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(42, null, filter.GetUtcStartDate(), filter.GetUtcEndDate()))
      .ReturnsAsync(new List<Transaction> { stale, fresh });

    var service = CreateService(repositoryMock);

    var result = (await service.GetTransactionsAsync(42, filter)).ToList();

    result.Should().ContainSingle();
    result.Single().Id.Should().Be(2);
    result.Single().UpdatedAt.Should().Be(fresh.UpdatedAt);
  }

  // Currency correctness (#941): the bank-reported ISO code must reach the DTO so clients can
  // stop silently summing foreign-currency amounts as dollars. Null (bank didn't report) stays null.
  [Fact]
  public async Task GetTransactionsAsync_MapsIsoCurrencyCode()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var filter = new TransactionFilterRequest { Page = 1, PageSize = 10 };

    var cad = CreateTransaction(1, 10, -20, TransactionCategory.Groceries, new DateTime(2024, 1, 5));
    cad.IsoCurrencyCode = "CAD";
    var unreported = CreateTransaction(2, 10, -30, TransactionCategory.Groceries, new DateTime(2024, 1, 6));

    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(42, null, filter.GetUtcStartDate(), filter.GetUtcEndDate()))
      .ReturnsAsync(new List<Transaction> { cad, unreported });

    var service = CreateService(repositoryMock);

    var result = (await service.GetTransactionsAsync(42, filter)).ToList();

    result.Should().HaveCount(2);
    result.Single(t => t.Id == 1).IsoCurrencyCode.Should().Be("CAD");
    result.Single(t => t.Id == 2).IsoCurrencyCode.Should().BeNull();
  }

  // Plaid hands back names that don't distinguish accounts — a real user had three cards all
  // named "CREDIT CARD", so every transaction looked like it hit the same card. The nickname is
  // the only thing telling them apart, so it has to reach the transaction DTO.
  [Fact]
  public async Task GetTransactionsAsync_AccountHasNickname_ReturnsNicknameAsAccountName()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var filter = new TransactionFilterRequest { Page = 1, PageSize = 10 };

    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(42, null, filter.StartDate, filter.EndDate))
      .ReturnsAsync(new List<Transaction>
      {
        CreateTransaction(1, 9, -25, TransactionCategory.Shopping, new DateTime(2024, 1, 5),
          accountName: "CREDIT CARD", accountNickname: "Travel Card")
      });

    var service = CreateService(repositoryMock);

    var result = (await service.GetTransactionsAsync(42, filter)).ToList();

    result.Single().AccountName.Should().Be("Travel Card");
  }

  [Fact]
  public async Task GetTransactionsAsync_AccountHasNoNickname_FallsBackToBankName()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var filter = new TransactionFilterRequest { Page = 1, PageSize = 10 };

    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(42, null, filter.StartDate, filter.EndDate))
      .ReturnsAsync(new List<Transaction>
      {
        CreateTransaction(1, 9, -25, TransactionCategory.Shopping, new DateTime(2024, 1, 5),
          accountName: "BuoyAI Checking")
      });

    var service = CreateService(repositoryMock);

    var result = (await service.GetTransactionsAsync(42, filter)).ToList();

    result.Single().AccountName.Should().Be("BuoyAI Checking");
  }

  // ---- Rename (Nickname) ----
  // Banks leave plenty of transactions with no usable name (Plaid sends no merchant for checks
  // or transfers), so renaming is the only way to make those rows readable. It's stored
  // alongside the bank's text, never over it.

  private static (TransactionService svc, Mock<ITransactionRepository> repo, Transaction tx) RenameHarness()
  {
    // A check: Plaid supplies no merchant, so the bank name comes from the description.
    var tx = CreateTransaction(1, 10, -25, TransactionCategory.Shopping, new DateTime(2024, 1, 5),
      description: "CHECK #1042", merchant: "", accountName: "Checking");
    var repo = new Mock<ITransactionRepository>();
    repo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(tx);
    repo.Setup(r => r.UpdateAsync(It.IsAny<Transaction>()))
      .ReturnsAsync((Transaction t) => t);
    return (CreateService(repo), repo, tx);
  }

  [Fact]
  public async Task UpdateTransactionNicknameAsync_SetsNicknameAndLeavesBankTextIntact()
  {
    var (svc, _, tx) = RenameHarness();

    var result = await svc.UpdateTransactionNicknameAsync(1, "Roof repair", 42);

    result.Nickname.Should().Be("Roof repair");
    result.DisplayName.Should().Be("Roof repair");
    // The bank's own text survives, so the rename stays reversible.
    tx.Description.Should().Be("CHECK #1042");
  }

  [Fact]
  public async Task UpdateTransactionNicknameAsync_TrimsWhitespace()
  {
    var (svc, _, _) = RenameHarness();

    var result = await svc.UpdateTransactionNicknameAsync(1, "  Roof repair  ", 42);

    result.Nickname.Should().Be("Roof repair");
  }

  [Fact]
  public async Task UpdateTransactionNicknameAsync_EmptyClearsItAndRestoresTheBankName()
  {
    var (svc, _, tx) = RenameHarness();
    tx.Nickname = "Old name";

    var result = await svc.UpdateTransactionNicknameAsync(1, "   ", 42);

    // Null, not "" — DisplayName treats blank as absent, but null is the honest "unset".
    result.Nickname.Should().BeNull();
    result.DisplayName.Should().Be("CHECK #1042");
  }

  [Fact]
  public async Task UpdateTransactionNicknameAsync_TooLong_ThrowsInvalidOperation()
  {
    // InvalidOperationException maps to 400; ArgumentException would surface as a 404 "not
    // found", which would be a lie for a too-long name.
    var (svc, _, _) = RenameHarness();

    var act = () => svc.UpdateTransactionNicknameAsync(1, new string('x', 201), 42);

    await act.Should().ThrowAsync<InvalidOperationException>();
  }

  [Fact]
  public async Task UpdateTransactionNicknameAsync_MissingTransaction_ThrowsArgument()
  {
    var repo = new Mock<ITransactionRepository>();
    repo.Setup(r => r.GetByIdAsync(999)).ReturnsAsync((Transaction?)null);

    var act = () => CreateService(repo).UpdateTransactionNicknameAsync(999, "x", 42);

    await act.Should().ThrowAsync<ArgumentException>();
  }

  [Fact]
  public async Task GetTransactionsAsync_ShouldPropagateExceptions()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var filter = new TransactionFilterRequest
    {
      StartDate = new DateTime(2024, 1, 1),
      EndDate = new DateTime(2024, 1, 31)
    };

    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(7, null, filter.StartDate, filter.EndDate))
      .ThrowsAsync(new InvalidOperationException("boom"));

    var service = CreateService(repositoryMock);

    var act = () => service.GetTransactionsAsync(7, filter);

    await act.Should().ThrowAsync<InvalidOperationException>();
  }

  [Fact]
  public async Task GetTransactionsWithPaginationAsync_ShouldReturnExpectedPageAndSummary()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var startDate = new DateTime(2024, 2, 1);
    var endDate = new DateTime(2024, 2, 28);
    var filter = new TransactionFilterRequest
    {
      StartDate = startDate,
      EndDate = endDate,
      Page = 2,
      PageSize = 2
    };

    var transactions = new List<Transaction>
    {
      CreateTransaction(1, 10, 500, TransactionCategory.Salary, startDate.AddDays(1)),
      CreateTransaction(2, 10, -50, TransactionCategory.Groceries, startDate.AddDays(2)),
      CreateTransaction(3, 10, -20, TransactionCategory.DiningOut, startDate.AddDays(3)),
      CreateTransaction(4, 10, -100, TransactionCategory.Utilities, startDate.AddDays(4)),
      CreateTransaction(5, 10, 200, TransactionCategory.Bonus, startDate.AddDays(5)),
      CreateTransaction(6, 10, -75, TransactionCategory.Groceries, startDate.AddDays(6))
    };

    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(99, null, It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
      .ReturnsAsync(transactions);

    var service = CreateService(repositoryMock);

    var response = await service.GetTransactionsWithPaginationAsync(99, filter);

    response.Transactions.Should().HaveCount(2);
    response.Transactions.Select(t => t.Id).Should().Equal(4, 3);
    response.Pagination.TotalItems.Should().Be(6);
    response.Pagination.TotalPages.Should().Be(3);
    response.Pagination.CurrentPage.Should().Be(2);
    response.Pagination.HasNextPage.Should().BeTrue();
    response.Pagination.HasPreviousPage.Should().BeTrue();

    response.Summary.TotalIncome.Should().Be(700);
    response.Summary.TotalExpenses.Should().Be(245);
    response.Summary.NetAmount.Should().Be(455);
    response.Summary.TransactionCount.Should().Be(6);
    response.Summary.CategoryTotals.Should().ContainKey("Groceries").WhoseValue.Should().Be(125);
    response.Summary.CategoryCounts.Should().ContainKey("Groceries").WhoseValue.Should().Be(2);
  }

  [Fact]
  public async Task GetTransactionsWithPaginationAsync_WithNoDates_ShouldQueryAnOpenRange()
  {
    // Regression: an unfiltered request used to be narrowed to UtcNow.AddMonths(-1) by
    // GetEffectiveStartDate. The app's "All" date chip sends no dates at all, so "All" silently
    // meant "last 30 days" — the list just stopped scrolling at the cutoff and older history was
    // unreachable. On production that hid the majority of real users' transactions.
    // No dates must mean no bounds.
    var repositoryMock = new Mock<ITransactionRepository>();
    var filter = new TransactionFilterRequest { Page = 1, PageSize = 10 };

    var twoYearsAgo = DateTime.UtcNow.AddYears(-2);
    var transactions = new List<Transaction>
    {
      CreateTransaction(1, 10, -25, TransactionCategory.Groceries, twoYearsAgo),
      CreateTransaction(2, 10, -30, TransactionCategory.Groceries, DateTime.UtcNow.AddDays(-1))
    };

    // Seeded non-null so a failure to capture shows up as a failure, not a false pass.
    DateTime? capturedStart = DateTime.UtcNow;
    DateTime? capturedEnd = DateTime.UtcNow;
    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(99, null, It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
      .Callback<int, int?, DateTime?, DateTime?>((_, _, start, end) =>
      {
        capturedStart = start;
        capturedEnd = end;
      })
      .ReturnsAsync(transactions);

    var service = CreateService(repositoryMock);

    var response = await service.GetTransactionsWithPaginationAsync(99, filter);

    capturedStart.Should().BeNull("a request with no start date must not be narrowed to a default window");
    capturedEnd.Should().BeNull("a request with no end date must not be narrowed to a default window");

    // The two-year-old row has to survive into both the page and the totals.
    response.Transactions.Select(t => t.Id).Should().Contain(1);
    response.Pagination.TotalItems.Should().Be(2);
    response.Summary.TransactionCount.Should().Be(2);
  }

  [Fact]
  public async Task GetTransactionsWithPaginationAsync_WithExplicitDates_ShouldForwardThemToTheRepository()
  {
    // The other direction of the same contract: callers that DO ask for a window (the AI context
    // builders pass a 30-day range) must still get exactly that window.
    var repositoryMock = new Mock<ITransactionRepository>();
    var start = new DateTime(2024, 2, 1);
    var end = new DateTime(2024, 2, 28);
    var filter = new TransactionFilterRequest
    {
      StartDate = start,
      EndDate = end,
      Page = 1,
      PageSize = 10
    };

    DateTime? capturedStart = null;
    DateTime? capturedEnd = null;
    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(99, null, It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
      .Callback<int, int?, DateTime?, DateTime?>((_, _, s, e) =>
      {
        capturedStart = s;
        capturedEnd = e;
      })
      .ReturnsAsync(new List<Transaction>());

    var service = CreateService(repositoryMock);

    await service.GetTransactionsWithPaginationAsync(99, filter);

    capturedStart.Should().Be(DateTime.SpecifyKind(start, DateTimeKind.Utc));
    capturedEnd.Should().Be(DateTime.SpecifyKind(end, DateTimeKind.Utc));
    // UTC kind matters: Npgsql rejects an Unspecified DateTime against a timestamptz column.
    capturedStart!.Value.Kind.Should().Be(DateTimeKind.Utc);
  }

  [Fact]
  public async Task GetTransactionSummaryAsync_ShouldAggregateIncomeAndExpenses()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var start = new DateTime(2024, 3, 1);
    var end = new DateTime(2024, 3, 31);

    var transactions = new List<Transaction>
    {
      CreateTransaction(1, 10, 1000, TransactionCategory.Salary, start.AddDays(1)),
      CreateTransaction(2, 10, -250, TransactionCategory.Rent, start.AddDays(2)),
      CreateTransaction(3, 10, -100, TransactionCategory.Groceries, start.AddDays(3)),
      CreateTransaction(4, 10, 150, TransactionCategory.OtherIncome, start.AddDays(4)),
      CreateTransaction(5, 10, -50, TransactionCategory.DiningOut, start.AddDays(5))
    };

    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(5, null, start, end))
      .ReturnsAsync(transactions);

    var service = CreateService(repositoryMock);

    var summary = await service.GetTransactionSummaryAsync(5, start, end);

    summary.TotalIncome.Should().Be(1150);
    summary.TotalExpenses.Should().Be(400);
    summary.NetAmount.Should().Be(750);
    summary.TransactionCount.Should().Be(5);
    summary.PeriodStart.Should().Be(start);
    summary.PeriodEnd.Should().Be(end);
    summary.CategoryTotals.Should().ContainKey("Rent/Mortgage").WhoseValue.Should().Be(250);
    summary.CategoryCounts.Should().ContainKey("Groceries").WhoseValue.Should().Be(1);
    // CategoryTotals is a SPENDING breakdown — income categories are excluded so the
    // AI context doesn't list Salary/Other Income as "top spending categories".
    summary.CategoryTotals.Should().NotContainKey("Salary");
    summary.CategoryTotals.Should().NotContainKey("Other Income");
  }

  [Fact]
  public async Task GetTransactionSummaryAsync_NetsRefundsWithinACategoryAndStaysSelfConsistent()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var start = new DateTime(2024, 5, 1);
    var end = new DateTime(2024, 5, 31);

    var transactions = new List<Transaction>
    {
      CreateTransaction(1, 10, -500, TransactionCategory.Groceries, start.AddDays(1)),
      CreateTransaction(2, 10, 200, TransactionCategory.Groceries, start.AddDays(2)),  // refund (Credit)
      CreateTransaction(3, 10, -100, TransactionCategory.DiningOut, start.AddDays(3)),
    };

    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(9, null, start, end))
      .ReturnsAsync(transactions);

    var service = CreateService(repositoryMock);

    var summary = await service.GetTransactionSummaryAsync(9, start, end);

    // Groceries nets the $200 refund out (matches the budget page), not a raw $500.
    summary.CategoryTotals[TransactionCategory.Groceries.GetDisplayName()].Should().Be(300);
    summary.CategoryTotals[TransactionCategory.DiningOut.GetDisplayName()].Should().Be(100);
    // TotalExpenses stays self-consistent with the category breakdown.
    summary.TotalExpenses.Should().Be(400);
    summary.CategoryTotals.Values.Sum().Should().Be(summary.TotalExpenses);
  }

  [Fact]
  public async Task GetCategoryStatisticsAsync_NetsRefunds()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var start = new DateTime(2024, 6, 1);
    var end = new DateTime(2024, 6, 30);

    var transactions = new List<Transaction>
    {
      CreateTransaction(1, 10, -300, TransactionCategory.Shopping, start.AddDays(1)),
      CreateTransaction(2, 10, 100, TransactionCategory.Shopping, start.AddDays(2)),  // refund
      CreateTransaction(3, 10, -100, TransactionCategory.Groceries, start.AddDays(3)),
    };

    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(9, null, start, end))
      .ReturnsAsync(transactions);

    var service = CreateService(repositoryMock);

    var stats = (await service.GetCategoryStatisticsAsync(9, start, end)).ToList();

    var shopping = stats.Single(s => s.CategoryId == (int)TransactionCategory.Shopping);
    shopping.TotalAmount.Should().Be(200);              // 300 - 100 refund
    shopping.Percentage.Should().BeApproximately(66.67m, 0.01m); // 200 / 300 total net spend
  }

  [Fact]
  public async Task GetCategoryStatisticsAsync_ShouldReturnOrderedPercentages()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var start = new DateTime(2024, 4, 1);
    var end = new DateTime(2024, 4, 30);

    var transactions = new List<Transaction>
    {
      CreateTransaction(1, 10, -100, TransactionCategory.Groceries, start.AddDays(1)),
      CreateTransaction(2, 10, -50, TransactionCategory.Groceries, start.AddDays(2)),
      CreateTransaction(3, 10, 400, TransactionCategory.Salary, start.AddDays(3)),        // income credit — excluded
      CreateTransaction(4, 10, -2000, TransactionCategory.Transfer, start.AddDays(4)),    // transfer — excluded
      CreateTransaction(5, 10, -500, TransactionCategory.Investment, start.AddDays(5)),   // DEBIT in an income category (e.g. investment purchase) — excluded
      CreateTransaction(6, 10, -25, TransactionCategory.DiningOut, start.AddDays(6))
    };

    repositoryMock
      .Setup(r => r.GetByUserOrFamilyAsync(8, null, start, end))
      .ReturnsAsync(transactions);

    var service = CreateService(repositoryMock);

    var stats = (await service.GetCategoryStatisticsAsync(8, start, end)).ToList();

    // Spending patterns = expenses only: Salary (income credit), Transfer (internal
    // movement) and the -500 Investment debit (a debit sitting in an INCOME category)
    // are all excluded, so percentages are of total SPEND (175), not total money movement.
    stats.Should().HaveCount(2);
    stats.Select(s => s.CategoryId).Should().NotContain((int)TransactionCategory.Salary);
    stats.Select(s => s.CategoryId).Should().NotContain((int)TransactionCategory.Transfer);
    stats.Select(s => s.CategoryId).Should().NotContain((int)TransactionCategory.Investment);
    stats[0].CategoryId.Should().Be((int)TransactionCategory.Groceries);
    stats[0].TotalAmount.Should().Be(150);
    stats[0].AverageAmount.Should().Be(75);
    stats[0].Percentage.Should().BeApproximately(85.71m, 0.01m); // 150 / 175
    stats[1].CategoryId.Should().Be((int)TransactionCategory.DiningOut);
    stats[1].TotalAmount.Should().Be(25);
    stats[1].TransactionCount.Should().Be(1);
    stats[1].Percentage.Should().BeApproximately(14.29m, 0.01m); // 25 / 175
  }

  [Fact]
  public async Task UpdateTransactionCategoryAsync_ShouldUpdateAndReturnDto()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var transaction = CreateTransaction(3, 12, -60, TransactionCategory.Groceries, DateTime.UtcNow);

    repositoryMock.Setup(r => r.GetByIdAsync(3)).ReturnsAsync(transaction);
    repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Transaction>()))
      .ReturnsAsync((Transaction t) => t);

    var service = CreateService(repositoryMock);

    var request = new UpdateTransactionCategoryRequest
    {
      CategoryId = (int)TransactionCategory.Transportation
    };

    var result = await service.UpdateTransactionCategoryAsync(3, request, 12);

    result.CategoryId.Should().Be((int)TransactionCategory.Transportation);
    result.CategoryName.Should().Be("Transportation");
    result.IsUserCategorized.Should().BeTrue();
    transaction.CategoryId.Should().Be((int)TransactionCategory.Transportation);
    repositoryMock.Verify(r => r.UpdateAsync(It.Is<Transaction>(t => t.IsUserCategorized)), Times.Once);
  }

  [Fact]
  public async Task UpdateTransactionCategoryAsync_WithUser_ShouldValidateOwnership()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var transaction = CreateTransaction(4, 20, -80, TransactionCategory.Groceries, DateTime.UtcNow, userId: 21);

    repositoryMock.Setup(r => r.GetByIdAsync(4)).ReturnsAsync(transaction);
    repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Transaction>()))
      .ReturnsAsync((Transaction t) => t);

    var service = CreateService(repositoryMock);

    var request = new UpdateTransactionCategoryRequest
    {
      CategoryId = (int)TransactionCategory.Utilities
    };

    var result = await service.UpdateTransactionCategoryAsync(4, request, 21);

    result.CategoryId.Should().Be((int)TransactionCategory.Utilities);
    transaction.CategoryId.Should().Be((int)TransactionCategory.Utilities);
  }

  [Fact]
  public async Task UpdateTransactionCategoryAsync_WithCustomCategory_ReturnsCustomCategoryName()
  {
    // Regression: assigning a custom category (id >= 1000) returned a DTO with
    // CategoryName "Uncategorized" because CategoryNav was not refreshed after the
    // FK change — the bug seen in the transaction review modal.
    var repositoryMock = new Mock<ITransactionRepository>();
    var transaction = CreateTransaction(7, 12, -120, TransactionCategory.Groceries, DateTime.UtcNow);

    repositoryMock.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(transaction);
    repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Transaction>()))
      .ReturnsAsync((Transaction t) => t);

    const int customCategoryId = 1042;
    var categoryRepoMock = new Mock<ICategoryRepository>();
    categoryRepoMock.Setup(r => r.GetByIdAsync(customCategoryId))
      .ReturnsAsync(new Category { Id = customCategoryId, Name = "Home Improvement" });

    var service = CreateService(repositoryMock, categoryRepositoryMock: categoryRepoMock);

    var request = new UpdateTransactionCategoryRequest { CategoryId = customCategoryId };

    var result = await service.UpdateTransactionCategoryAsync(7, request, 12);

    result.CategoryId.Should().Be(customCategoryId);
    result.CategoryName.Should().Be("Home Improvement");
  }

  [Fact]
  public async Task UpdateTransactionCategoryAsync_ShouldThrowWhenNotFound()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    repositoryMock.Setup(r => r.GetByIdAsync(55)).ReturnsAsync((Transaction?)null);

    var service = CreateService(repositoryMock);

    var act = () => service.UpdateTransactionCategoryAsync(55, new UpdateTransactionCategoryRequest
    {
      CategoryId = (int)TransactionCategory.Travel
    }, 1);

    await act.Should().ThrowAsync<ArgumentException>().WithMessage("Transaction not found");
  }

  [Fact]
  public async Task UpdateTransactionCategoryAsync_WithUser_ShouldThrowWhenAuthServiceDeniesAccess()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var transaction = CreateTransaction(6, 30, -45, TransactionCategory.Groceries, DateTime.UtcNow, userId: 90);

    repositoryMock.Setup(r => r.GetByIdAsync(6)).ReturnsAsync(transaction);

    // Create auth service mock that denies access (simulates RBAC denial)
    var authServiceMock = new Mock<IFamilyAuthorizationService>();
    authServiceMock.Setup(s => s.ValidateTransactionAccessOrThrowAsync(91, 6, ResourcePermission.Edit))
        .ThrowsAsync(new UnauthorizedAccessException("You don't have permission to modify this transaction"));

    var service = CreateService(repositoryMock, authServiceMock: authServiceMock);

    var act = () => service.UpdateTransactionCategoryAsync(6, new UpdateTransactionCategoryRequest
    {
      CategoryId = (int)TransactionCategory.Healthcare
    }, userId: 91);

    await act.Should().ThrowAsync<UnauthorizedAccessException>()
      .WithMessage("You don't have permission to modify this transaction");
  }

  [Fact]
  public async Task UpdateTransactionCategoryAsync_LinkedPersonalChildCategory_SetsPersonalSubCategoryForOwner()
  {
    // RFC #793 B6 — SET eligibility for a linked Type=Personal child. The family funding category
    // keeps its REAL CategoryId (e.g. Groceries) with IsAllowance=true + AssignedUserId=<member>.
    // The assigned member categorizing a transaction to that category may set its private
    // PersonalUserCategoryId, exactly like a Type=Allowance category (which parks on Uncategorized).
    var memberId = 2;
    var familyId = 7;
    var familyCategoryId = (int)TransactionCategory.Groceries;   // a REAL category id, not 99

    var repositoryMock = new Mock<ITransactionRepository>();
    var transaction = CreateTransaction(9, 30, -60, TransactionCategory.Groceries, DateTime.UtcNow, userId: memberId);
    repositoryMock.Setup(r => r.GetByIdAsync(9)).ReturnsAsync(transaction);
    repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Transaction>())).ReturnsAsync((Transaction t) => t);

    var userRepoMock = new Mock<IUserRepository>();
    userRepoMock.Setup(r => r.GetByIdAsync(memberId)).ReturnsAsync(new User { Id = memberId, FamilyId = familyId });

    // The family budget carries the linked funding category with its real CategoryId.
    var familyBudget = new Budget
    {
      Id = 100, FamilyId = familyId, Type = BudgetType.Family,
      Categories = new List<BudgetCategory>
      {
        new()
        {
          Id = 55, BudgetId = 100, CategoryId = familyCategoryId,
          IsAllowance = true, LinkedBudgetId = 200, AssignedUserId = memberId
        }
      }
    };
    var budgetRepoMock = new Mock<IBudgetRepository>();
    budgetRepoMock.Setup(r => r.GetByFamilyIdAsync(familyId)).ReturnsAsync(new List<Budget> { familyBudget });

    var service = CreateService(repositoryMock, userRepositoryMock: userRepoMock, budgetRepositoryMock: budgetRepoMock);

    var request = new UpdateTransactionCategoryRequest { CategoryId = familyCategoryId, PersonalCategoryId = 42 };

    var result = await service.UpdateTransactionCategoryAsync(9, request, memberId);

    // The owner's private sub-category is stored and the charge is claimed for them.
    transaction.PersonalUserCategoryId.Should().Be(42);
    transaction.ClaimedByUserId.Should().Be(memberId);
  }

  [Fact]
  public async Task SplitTransactionAsync_CopiesAllowanceAttributionToChildren()
  {
    // A charge claimed to a child's allowance (ClaimedByUserId) and privately sub-categorized
    // (PersonalUserCategoryId) must keep that attribution on its split children, otherwise the
    // split silently drops the whole amount from the allowance's "spent".
    var repositoryMock = new Mock<ITransactionRepository>();
    var parent = CreateTransaction(50, 12, -60, TransactionCategory.Groceries, DateTime.UtcNow, userId: 7);
    parent.ClaimedByUserId = 7;
    parent.PersonalUserCategoryId = 3;
    parent.SavingsGoalId = null;
    parent.BudgetId = 15;

    repositoryMock.Setup(r => r.GetByIdWithSplitsAsync(50)).ReturnsAsync(parent);
    repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Transaction>())).ReturnsAsync((Transaction t) => t);

    var createdChildren = new List<Transaction>();
    repositoryMock.Setup(r => r.CreateManyAsync(It.IsAny<IEnumerable<Transaction>>()))
      .ReturnsAsync((IEnumerable<Transaction> ts) => ts.ToList())
      .Callback<IEnumerable<Transaction>>(ts => createdChildren.AddRange(ts));

    var service = CreateService(repositoryMock);

    var request = new SplitTransactionRequest
    {
      Splits = new List<TransactionSplitItem>
      {
        new() { Amount = 40, CategoryId = (int)TransactionCategory.Entertainment },
        new() { Amount = 20, CategoryId = (int)TransactionCategory.DiningOut }
      }
    };

    await service.SplitTransactionAsync(50, request, 7);

    createdChildren.Should().HaveCount(2);
    createdChildren.Should().OnlyContain(c => c.ClaimedByUserId == 7);
    createdChildren.Should().OnlyContain(c => c.PersonalUserCategoryId == 3);
    createdChildren.Should().OnlyContain(c => c.BudgetId == 15);
    createdChildren.Should().OnlyContain(c => c.ParentTransactionId == 50);
    // Expense parent → children stay negative and sum to the original amount.
    createdChildren.Sum(c => c.Amount).Should().Be(-60);
  }

  [Fact]
  public async Task SplitTransactionAsync_RejectsMoreThanMaxSplits()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var parent = CreateTransaction(60, 12, -51, TransactionCategory.Groceries, DateTime.UtcNow, userId: 7);
    repositoryMock.Setup(r => r.GetByIdWithSplitsAsync(60)).ReturnsAsync(parent);

    var service = CreateService(repositoryMock);

    // 51 splits of $1 each — sums to the parent amount but exceeds the 50-item cap.
    var request = new SplitTransactionRequest
    {
      Splits = Enumerable.Range(0, 51)
        .Select(_ => new TransactionSplitItem { Amount = 1, CategoryId = (int)TransactionCategory.Groceries })
        .ToList()
    };

    var act = () => service.SplitTransactionAsync(60, request, 7);

    await act.Should().ThrowAsync<ArgumentException>().WithMessage("*at most*");
    // No child rows written when the request is rejected.
    repositoryMock.Verify(r => r.CreateManyAsync(It.IsAny<IEnumerable<Transaction>>()), Times.Never);
  }

  [Fact]
  public async Task SplitTransactionAsync_RejectsNonPositiveSplitAmount()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var parent = CreateTransaction(61, 12, -60, TransactionCategory.Groceries, DateTime.UtcNow, userId: 7);
    repositoryMock.Setup(r => r.GetByIdWithSplitsAsync(61)).ReturnsAsync(parent);

    var service = CreateService(repositoryMock);

    var request = new SplitTransactionRequest
    {
      Splits = new List<TransactionSplitItem>
      {
        new() { Amount = 60, CategoryId = (int)TransactionCategory.Groceries },
        new() { Amount = 0, CategoryId = (int)TransactionCategory.DiningOut }
      }
    };

    var act = () => service.SplitTransactionAsync(61, request, 7);

    await act.Should().ThrowAsync<ArgumentException>().WithMessage("*greater than zero*");
    repositoryMock.Verify(r => r.CreateManyAsync(It.IsAny<IEnumerable<Transaction>>()), Times.Never);
  }

  [Fact]
  public async Task UnsplitTransactionAsync_ReversesEarmarkOnEarmarkedChildBeforeDelete()
  {
    const int goalId = 9;
    const int childId = 71;
    var parent = CreateTransaction(70, 12, -100, TransactionCategory.Groceries, DateTime.UtcNow, userId: 7);
    parent.IsSplit = true;
    var earmarkedChild = CreateTransaction(childId, 12, -50, TransactionCategory.Groceries, DateTime.UtcNow, userId: 7);
    earmarkedChild.ParentTransactionId = 70;
    earmarkedChild.SavingsGoalId = goalId;
    var plainChild = CreateTransaction(72, 12, -50, TransactionCategory.Groceries, DateTime.UtcNow, userId: 7);
    plainChild.ParentTransactionId = 70;
    parent.SplitTransactions = new List<Transaction> { earmarkedChild, plainChild };

    var repositoryMock = new Mock<ITransactionRepository>();
    repositoryMock.Setup(r => r.GetByIdWithSplitsAsync(70)).ReturnsAsync(parent);
    repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Transaction>())).ReturnsAsync((Transaction t) => t);

    var reversedBeforeDelete = false;
    var deleted = false;
    repositoryMock.Setup(r => r.DeleteAsync(childId))
      .Returns(Task.CompletedTask)
      .Callback(() => deleted = true);

    var savingsMock = new Mock<ISavingsService>();
    savingsMock.Setup(s => s.UndoWithdrawalAsync(goalId, childId))
      .ReturnsAsync(new SavingsGoalDto())
      .Callback(() => { if (!deleted) reversedBeforeDelete = true; });
    savingsMock.Setup(s => s.UndoDepositAsync(goalId, childId))
      .ThrowsAsync(new ArgumentException("no deposit"));

    var service = CreateService(repositoryMock, savingsServiceMock: savingsMock);

    await service.UnsplitTransactionAsync(70, 7);

    // The earmarked child's goal allocation is reversed, and before the child row is removed.
    savingsMock.Verify(s => s.UndoWithdrawalAsync(goalId, childId), Times.Once);
    reversedBeforeDelete.Should().BeTrue();
    repositoryMock.Verify(r => r.DeleteAsync(childId), Times.Once);
    repositoryMock.Verify(r => r.DeleteAsync(72), Times.Once);
    // Parent un-flagged.
    parent.IsSplit.Should().BeFalse();
  }

  [Fact]
  public async Task DeleteTransactionAsync_RejectsDeletingSplitChild()
  {
    const int childId = 81;
    var child = CreateTransaction(childId, 12, -25, TransactionCategory.Groceries, DateTime.UtcNow, userId: 7);
    child.ParentTransactionId = 80; // it is a split child

    var repositoryMock = new Mock<ITransactionRepository>();
    repositoryMock.Setup(r => r.GetByIdWithSplitsAsync(childId)).ReturnsAsync(child);

    var service = CreateService(repositoryMock);

    var act = () => service.DeleteTransactionAsync(childId, 7);

    await act.Should().ThrowAsync<ArgumentException>().WithMessage("*split item*");
    // The child row is NOT deleted — that would leave the parent inconsistent.
    repositoryMock.Verify(r => r.DeleteAsync(It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task ResetAndResyncTransactionsAsync_ShouldDeleteExistingTransactions()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    repositoryMock.Setup(r => r.DeleteAllByUserIdAsync(33)).Returns(Task.CompletedTask);

    var service = CreateService(repositoryMock);

    await service.ResetAndResyncTransactionsAsync(33);

    repositoryMock.Verify(r => r.DeleteAllByUserIdAsync(33), Times.Once);
  }

  [Fact]
  public async Task ResetAndResyncTransactionsAsync_ShouldPropagateErrors()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    repositoryMock.Setup(r => r.DeleteAllByUserIdAsync(77)).ThrowsAsync(new InvalidOperationException("fail"));

    var service = CreateService(repositoryMock);

    var act = () => service.ResetAndResyncTransactionsAsync(77);

    await act.Should().ThrowAsync<InvalidOperationException>();
  }

  // Regression: a Debit transaction in a CUSTOM income-flagged category (CategoryNav.IsIncome==true,
  // unified id >= 1000) is booked on apply as a WITHDRAWAL — the controller's IsIncomeTransaction only
  // treats Type==Credit or a SYSTEM income id (1-4) as income, so the custom flag is ignored. The
  // reversal must undo that withdrawal. Re-deriving the direction from the entity IsIncome() (which
  // DOES honor the custom flag) would instead try UndoDepositAsync, find no deposit allocation, and
  // silently strand the goal drift. Reversal is by persisted source tag, so it undoes the withdrawal.
  [Fact]
  public async Task DeleteTransactionAsync_DebitInCustomIncomeCategory_ReversesWithdrawalNotDeposit()
  {
    const int userId = 1;
    const int transactionId = 900;
    const int goalId = 42;

    var transaction = new Transaction
    {
      Id = transactionId,
      AccountId = 10,
      Amount = -150m,
      Type = TransactionType.Debit,
      Description = "Bali fund spend",
      CategoryId = 1500, // custom (unified id >= 1000), NOT a system income id
      CategoryNav = new Category { Id = 1500, Name = "Side Gig", IsIncome = true },
      SavingsGoalId = goalId,
      Account = new Account { Id = 10, UserId = userId, Name = "Checking" }
    };
    // Sanity: the entity treats this as income (the trap the old reversal fell into).
    transaction.IsIncome().Should().BeTrue();

    var repositoryMock = new Mock<ITransactionRepository>();
    repositoryMock.Setup(r => r.GetByIdWithSplitsAsync(transactionId)).ReturnsAsync(transaction);
    repositoryMock.Setup(r => r.DeleteAsync(transactionId)).Returns(Task.CompletedTask);

    var savingsMock = new Mock<ISavingsService>();
    // Reality for a withdrawal earmark: the withdrawal allocation exists; there is NO deposit
    // allocation, so probing the deposit undo throws ArgumentException (as the real service does).
    savingsMock.Setup(s => s.UndoWithdrawalAsync(goalId, transactionId))
      .ReturnsAsync(new SavingsGoalDto());
    savingsMock.Setup(s => s.UndoDepositAsync(goalId, transactionId))
      .ThrowsAsync(new ArgumentException($"No deposit found for transaction {transactionId} on goal {goalId}."));

    var service = CreateService(repositoryMock, savingsServiceMock: savingsMock);

    await service.DeleteTransactionAsync(transactionId, userId);

    // The withdrawal is undone (goal balance restored) despite IsIncome()==true...
    savingsMock.Verify(s => s.UndoWithdrawalAsync(goalId, transactionId), Times.Once);
    // ...and the "no deposit" probe does not abort the delete.
    repositoryMock.Verify(r => r.DeleteAsync(transactionId), Times.Once);
  }

  [Fact]
  public async Task ResetAndResyncTransactionsAsync_ReversesEarmarksBeforeBulkDelete()
  {
    const int userId = 55;
    const int earmarkedId = 700;
    const int goalId = 9;

    var earmarked = new Transaction
    {
      Id = earmarkedId,
      AccountId = 10,
      Amount = -80m,
      Type = TransactionType.Debit,
      CategoryId = (int)TransactionCategory.Groceries,
      SavingsGoalId = goalId,
      Account = new Account { Id = 10, UserId = userId, Name = "Checking" }
    };
    var plain = new Transaction
    {
      Id = 701,
      AccountId = 10,
      Amount = -20m,
      Type = TransactionType.Debit,
      CategoryId = (int)TransactionCategory.DiningOut,
      SavingsGoalId = null,
      Account = new Account { Id = 10, UserId = userId, Name = "Checking" }
    };

    var repositoryMock = new Mock<ITransactionRepository>();
    repositoryMock.Setup(r => r.GetByUserIdAsync(userId, null, null))
      .ReturnsAsync(new[] { earmarked, plain });

    var reversalBeforeDelete = false;
    var deleted = false;
    var savingsMock = new Mock<ISavingsService>();
    savingsMock.Setup(s => s.UndoWithdrawalAsync(goalId, earmarkedId))
      .ReturnsAsync(new SavingsGoalDto())
      .Callback(() => { if (!deleted) reversalBeforeDelete = true; });
    savingsMock.Setup(s => s.UndoDepositAsync(goalId, earmarkedId))
      .ThrowsAsync(new ArgumentException("No deposit"));
    repositoryMock.Setup(r => r.DeleteAllByUserIdAsync(userId))
      .Returns(Task.CompletedTask)
      .Callback(() => deleted = true);

    var service = CreateService(repositoryMock, savingsServiceMock: savingsMock);

    await service.ResetAndResyncTransactionsAsync(userId);

    // The earmarked transaction's goal balance is restored...
    savingsMock.Verify(s => s.UndoWithdrawalAsync(goalId, earmarkedId), Times.Once);
    // ...the non-earmarked one is never reversed...
    savingsMock.Verify(s => s.UndoWithdrawalAsync(It.IsAny<int>(), 701), Times.Never);
    savingsMock.Verify(s => s.UndoDepositAsync(It.IsAny<int>(), 701), Times.Never);
    // ...and reversal happens BEFORE the bulk delete drops the transactions.
    reversalBeforeDelete.Should().BeTrue();
    repositoryMock.Verify(r => r.DeleteAllByUserIdAsync(userId), Times.Once);
  }

  [Fact]
  public async Task CreateManualTransactionAsync_WithValidExpense_CreatesTransactionOnOwnedAccount()
  {
    const int userId = 1;
    const int accountId = 10;
    var repositoryMock = new Mock<ITransactionRepository>();
    var accountRepositoryMock = new Mock<IAccountRepository>();
    accountRepositoryMock.Setup(r => r.GetByUserOrFamilyAsync(userId, It.IsAny<int?>()))
      .ReturnsAsync(new[] { new Account { Id = accountId, UserId = userId, Name = "Checking" } });

    Transaction? captured = null;
    repositoryMock.Setup(r => r.AddAsync(It.IsAny<Transaction>()))
      .ReturnsAsync((Transaction t) => { t.Id = 123; captured = t; return t; });
    repositoryMock.Setup(r => r.GetByIdWithSplitsAsync(123))
      .ReturnsAsync(() =>
      {
        captured!.Account = new Account { Id = accountId, UserId = userId, Name = "Checking" };
        return captured;
      });

    var service = CreateService(repositoryMock, accountRepositoryMock: accountRepositoryMock);
    var request = new CreateTransactionRequest
    {
      AccountId = accountId,
      Amount = -12.50m,
      Description = "Coffee",
      MerchantName = "Cafe",
      Date = new DateTime(2026, 7, 9),
      CategoryId = (int)TransactionCategory.DiningOut,
    };

    var result = await service.CreateManualTransactionAsync(request, userId);

    result.Amount.Should().Be(-12.50m);
    result.Type.Should().Be(TransactionType.Debit);
    result.Description.Should().Be("Coffee");
    result.AccountName.Should().Be("Checking");
    captured!.PlaidTransactionId.Should().StartWith("manual_");
    captured.CategorySource.Should().Be("User");
    repositoryMock.Verify(r => r.AddAsync(It.IsAny<Transaction>()), Times.Once);
  }

  [Fact]
  public async Task CreateManualTransactionAsync_ReadOnlyMember_ThrowsUnauthorized()
  {
    const int userId = 1;
    var repositoryMock = new Mock<ITransactionRepository>();
    var authMock = CreateDefaultAuthServiceMock();
    authMock.Setup(s => s.CanModifyFinancialDataAsync(userId)).ReturnsAsync(false);

    var service = CreateService(repositoryMock, authServiceMock: authMock);
    var request = new CreateTransactionRequest
    {
      AccountId = 10,
      Amount = -5m,
      Description = "Snack",
      Date = new DateTime(2026, 7, 9),
    };

    var act = () => service.CreateManualTransactionAsync(request, userId);

    await act.Should().ThrowAsync<UnauthorizedAccessException>();
    repositoryMock.Verify(r => r.AddAsync(It.IsAny<Transaction>()), Times.Never);
  }

  [Fact]
  public async Task CreateManualTransactionAsync_AccountNotOwned_ThrowsArgument()
  {
    const int userId = 1;
    var repositoryMock = new Mock<ITransactionRepository>();
    var accountRepositoryMock = new Mock<IAccountRepository>();
    // The user's accounts do NOT include the requested account (999).
    accountRepositoryMock.Setup(r => r.GetByUserOrFamilyAsync(userId, It.IsAny<int?>()))
      .ReturnsAsync(new[] { new Account { Id = 10, UserId = userId, Name = "Checking" } });

    var service = CreateService(repositoryMock, accountRepositoryMock: accountRepositoryMock);
    var request = new CreateTransactionRequest
    {
      AccountId = 999,
      Amount = -5m,
      Description = "Snack",
      Date = new DateTime(2026, 7, 9),
    };

    var act = () => service.CreateManualTransactionAsync(request, userId);

    await act.Should().ThrowAsync<ArgumentException>();
    repositoryMock.Verify(r => r.AddAsync(It.IsAny<Transaction>()), Times.Never);
  }

  [Fact]
  public async Task CreateManualTransactionAsync_NoAccountId_CreatesAndFilesUnderCashAccount()
  {
    const int userId = 1;
    const int cashAccountId = 77;
    var repositoryMock = new Mock<ITransactionRepository>();
    var accountRepositoryMock = new Mock<IAccountRepository>();
    // User has no accounts yet — the Cash account is created on demand.
    accountRepositoryMock.Setup(r => r.GetByUserOrFamilyAsync(userId, It.IsAny<int?>()))
      .ReturnsAsync(Array.Empty<Account>());
    Account? createdAccount = null;
    accountRepositoryMock.Setup(r => r.CreateAsync(It.IsAny<Account>()))
      .ReturnsAsync((Account a) => { a.Id = cashAccountId; createdAccount = a; return a; });

    Transaction? captured = null;
    repositoryMock.Setup(r => r.AddAsync(It.IsAny<Transaction>()))
      .ReturnsAsync((Transaction t) => { t.Id = 200; captured = t; return t; });
    repositoryMock.Setup(r => r.GetByIdWithSplitsAsync(200))
      .ReturnsAsync(() => { captured!.Account = createdAccount!; return captured; });

    var service = CreateService(repositoryMock, accountRepositoryMock: accountRepositoryMock);
    var request = new CreateTransactionRequest
    {
      AccountId = null, // no account → Cash
      Amount = -8m,
      Description = "Farmers market",
      Date = new DateTime(2026, 7, 9),
    };

    await service.CreateManualTransactionAsync(request, userId);

    createdAccount.Should().NotBeNull();
    createdAccount!.Type.Should().Be(AccountType.Cash);
    createdAccount.PlaidAccountId.Should().Be($"manual-cash-{userId}");
    captured!.AccountId.Should().Be(cashAccountId);
    accountRepositoryMock.Verify(r => r.CreateAsync(It.IsAny<Account>()), Times.Once);
  }

  [Fact]
  public async Task CreateManualTransactionAsync_NoAccountId_ReusesExistingCashAccount()
  {
    const int userId = 1;
    const int cashAccountId = 55;
    var repositoryMock = new Mock<ITransactionRepository>();
    var accountRepositoryMock = new Mock<IAccountRepository>();
    accountRepositoryMock.Setup(r => r.GetByUserOrFamilyAsync(userId, It.IsAny<int?>()))
      .ReturnsAsync(new[] { new Account { Id = cashAccountId, UserId = userId, Type = AccountType.Cash, Name = "Cash" } });

    Transaction? captured = null;
    repositoryMock.Setup(r => r.AddAsync(It.IsAny<Transaction>()))
      .ReturnsAsync((Transaction t) => { t.Id = 201; captured = t; return t; });
    repositoryMock.Setup(r => r.GetByIdWithSplitsAsync(201))
      .ReturnsAsync(() =>
      {
        captured!.Account = new Account { Id = cashAccountId, UserId = userId, Type = AccountType.Cash, Name = "Cash" };
        return captured;
      });

    var service = CreateService(repositoryMock, accountRepositoryMock: accountRepositoryMock);
    var request = new CreateTransactionRequest
    {
      AccountId = null,
      Amount = 20m,
      Description = "Cash gift",
      Date = new DateTime(2026, 7, 9),
    };

    await service.CreateManualTransactionAsync(request, userId);

    captured!.AccountId.Should().Be(cashAccountId);
    accountRepositoryMock.Verify(r => r.CreateAsync(It.IsAny<Account>()), Times.Never);
  }

  [Fact]
  public async Task CreateManualTransactionAsync_ZeroAmount_ThrowsArgument()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var service = CreateService(repositoryMock);
    var request = new CreateTransactionRequest
    {
      AccountId = 10,
      Amount = 0m,
      Description = "Nothing",
      Date = new DateTime(2026, 7, 9),
    };

    var act = () => service.CreateManualTransactionAsync(request, 1);

    await act.Should().ThrowAsync<ArgumentException>();
    repositoryMock.Verify(r => r.AddAsync(It.IsAny<Transaction>()), Times.Never);
  }

  [Theory]
  [InlineData(-999999999999.99)] // ~ -$1 trillion
  [InlineData(1000000000.01)]    // just over the $1B cap
  public async Task CreateManualTransactionAsync_AmountOverCap_ThrowsArgument(decimal amount)
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var service = CreateService(repositoryMock);
    var request = new CreateTransactionRequest
    {
      AccountId = 10,
      Amount = amount,
      Description = "Too big",
      Date = new DateTime(2026, 7, 9),
    };

    var act = () => service.CreateManualTransactionAsync(request, 1);

    await act.Should().ThrowAsync<ArgumentException>();
    repositoryMock.Verify(r => r.AddAsync(It.IsAny<Transaction>()), Times.Never);
  }

  [Fact]
  public async Task CreateManualTransactionAsync_DescriptionTooLong_ThrowsArgument()
  {
    var repositoryMock = new Mock<ITransactionRepository>();
    var service = CreateService(repositoryMock);
    var request = new CreateTransactionRequest
    {
      AccountId = 10,
      Amount = -5m,
      Description = new string('x', 201),
      Date = new DateTime(2026, 7, 9),
    };

    var act = () => service.CreateManualTransactionAsync(request, 1);

    await act.Should().ThrowAsync<ArgumentException>();
    repositoryMock.Verify(r => r.AddAsync(It.IsAny<Transaction>()), Times.Never);
  }

  [Fact]
  public async Task UpdateTransactionCategoryAsync_UnknownCustomCategory_ThrowsArgument()
  {
    // QE #615: an unknown custom-category id previously hit the FK constraint and 500'd.
    var repositoryMock = new Mock<ITransactionRepository>();
    repositoryMock.Setup(r => r.GetByIdAsync(5))
      .ReturnsAsync(CreateTransaction(5, 10, -20m, TransactionCategory.Groceries, new DateTime(2026, 7, 1)));

    var catRepoMock = new Mock<ICategoryRepository>();
    catRepoMock.Setup(r => r.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((Category?)null); // 99999 doesn't exist

    var service = CreateService(repositoryMock, categoryRepositoryMock: catRepoMock);
    var request = new UpdateTransactionCategoryRequest { CategoryId = 99999 };

    var act = () => service.UpdateTransactionCategoryAsync(5, request, 1);

    await act.Should().ThrowAsync<ArgumentException>();
    repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Transaction>()), Times.Never);
  }
}
