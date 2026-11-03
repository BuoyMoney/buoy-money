using Xunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class WrappedServiceTests : IDisposable
{
  private readonly BuoyAIDbContext _context;
  private readonly WrappedService _service;
  private int _accountId;
  private const int UserId = 1;

  public WrappedServiceTests()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;
    _context = new BuoyAIDbContext(options);
    _service = new WrappedService(_context, new TransactionRepository(_context));
    Seed();
  }

  public void Dispose() => _context.Dispose();

  private void Seed()
  {
    var user = new User
    {
      Id = UserId,
      Username = "wren",
      Email = "wren@test.local",
      PasswordHash = "x",
      Currency = "USD",
    };
    _context.Users.Add(user);
    var account = new Account
    {
      UserId = UserId,
      PlaidAccountId = "acc-1",
      Name = "Checking",
      Type = AccountType.Checking,
      Currency = "USD",
      IsActive = true,
    };
    _context.Accounts.Add(account);
    _context.SaveChanges();
    _accountId = account.Id;
  }

  private Transaction Tx(decimal amount, TransactionType type, TransactionCategory cat,
      DateTime date, string merchant = "", bool pending = false)
  {
    return new Transaction
    {
      AccountId = _accountId,
      PlaidTransactionId = Guid.NewGuid().ToString("N"),
      Amount = Math.Abs(amount),
      Type = type,
      Description = merchant,
      MerchantName = merchant,
      CategoryId = (int)cat,
      Date = date,
      IsPending = pending,
      CreatedAt = date,
      UpdatedAt = date,
    };
  }

  private async Task SeedJulyAsync()
  {
    _context.Transactions.AddRange(
      Tx(3800m, TransactionType.Credit, TransactionCategory.Salary, new DateTime(2026, 7, 1), "Coastal Studio"),
      Tx(3800m, TransactionType.Credit, TransactionCategory.Salary, new DateTime(2026, 7, 15), "Coastal Studio"),
      // Dining: 3 visits, top category ($180 total)
      Tx(60m, TransactionType.Debit, TransactionCategory.DiningOut, new DateTime(2026, 7, 2), "Saltline Bistro"),
      Tx(70m, TransactionType.Debit, TransactionCategory.DiningOut, new DateTime(2026, 7, 10), "Saltline Bistro"),
      Tx(50m, TransactionType.Debit, TransactionCategory.DiningOut, new DateTime(2026, 7, 20), "Nori Sushi"),
      // Groceries: $120 on one day
      Tx(120m, TransactionType.Debit, TransactionCategory.Groceries, new DateTime(2026, 7, 10), "Trader Joe's"),
      // Transfer must not count as spend
      Tx(500m, TransactionType.Debit, TransactionCategory.Transfer, new DateTime(2026, 7, 16), "Internal Transfer"),
      // Pending must be ignored entirely
      Tx(999m, TransactionType.Debit, TransactionCategory.Shopping, new DateTime(2026, 7, 25), "Amazon", pending: true));
    await _context.SaveChangesAsync();
  }

  [Fact]
  public async Task Monthly_ComputesTotalsTopCategoryAndMerchant()
  {
    await SeedJulyAsync();

    var dto = await _service.GetMonthlyAsync(UserId, 2026, 7);

    dto.Should().NotBeNull();
    dto!.TotalIncome.Should().Be(7600m);
    dto.TotalSpent.Should().Be(300m); // dining 180 + groceries 120; transfer + pending excluded
    dto.TopCategory!.Name.Should().Be("DiningOut");
    dto.TopCategory.Amount.Should().Be(180m);
    dto.TopMerchant!.Name.Should().Be("Saltline Bistro");
    dto.TopMerchant.Visits.Should().Be(2);
    dto.BiggestSpendDay!.Date.Should().Be(new DateTime(2026, 7, 10));
    dto.BiggestSpendDay.Amount.Should().Be(190m);
  }

  [Fact]
  public async Task Monthly_CountsZeroSpendDays()
  {
    await SeedJulyAsync();

    var dto = await _service.GetMonthlyAsync(UserId, 2026, 7);

    // 31 days in July; budgetable spend happened on 3 distinct days
    // (transfer day + pending day don't count as spend days).
    dto!.ZeroSpendDays.Should().Be(28);
  }

  [Fact]
  public async Task Monthly_ReturnsNullWhenNoActivity()
  {
    await SeedJulyAsync();

    var dto = await _service.GetMonthlyAsync(UserId, 2026, 3);

    dto.Should().BeNull();
  }

  [Fact]
  public async Task Monthly_ReportsPreviousMonthDelta()
  {
    await SeedJulyAsync();
    _context.Transactions.Add(
      Tx(200m, TransactionType.Debit, TransactionCategory.Groceries, new DateTime(2026, 6, 12), "Kroger"));
    await _context.SaveChangesAsync();

    var dto = await _service.GetMonthlyAsync(UserId, 2026, 7);

    dto!.PreviousMonthSpent.Should().Be(200m);
  }

  [Fact]
  public async Task Monthly_IncludesBadgesEarnedInMonth()
  {
    await SeedJulyAsync();
    _context.Set<UserAchievement>().AddRange(
      new UserAchievement
      {
        UserId = UserId,
        AchievementKey = "goal_achiever",
        Title = "Goal Achiever",
        Icon = "⭐",
        Category = "savings",
        Points = 200,
        EarnedAt = new DateTime(2026, 7, 9),
      },
      new UserAchievement
      {
        UserId = UserId,
        AchievementKey = "first_budget",
        Title = "First Budget",
        Icon = "🎯",
        Category = "budget",
        Points = 50,
        EarnedAt = new DateTime(2026, 6, 2), // outside the month
      });
    await _context.SaveChangesAsync();

    var dto = await _service.GetMonthlyAsync(UserId, 2026, 7);

    dto!.BadgesEarned.Should().ContainSingle(b => b.Key == "goal_achiever");
  }

  [Fact]
  public async Task Yearly_AggregatesAcrossMonthsAndPicksBestBadge()
  {
    await SeedJulyAsync();
    _context.Transactions.Add(
      Tx(80m, TransactionType.Debit, TransactionCategory.Shopping, new DateTime(2026, 3, 5), "Target"));
    _context.Set<UserAchievement>().AddRange(
      new UserAchievement
      {
        UserId = UserId,
        AchievementKey = "hundred_club",
        Title = "$100 Club",
        Icon = "💯",
        Category = "savings",
        Points = 75,
        EarnedAt = new DateTime(2026, 2, 1),
      },
      new UserAchievement
      {
        UserId = UserId,
        AchievementKey = "savings_milestone",
        Title = "Thousand Dollar Saver",
        Icon = "🏅",
        Category = "savings",
        Points = 300,
        EarnedAt = new DateTime(2026, 7, 20),
      });
    await _context.SaveChangesAsync();

    var dto = await _service.GetYearlyAsync(UserId, 2026);

    dto.Should().NotBeNull();
    dto!.TotalSpent.Should().Be(380m);
    dto.MonthlySpend.Should().HaveCount(12);
    dto.MonthlySpend[2].Should().Be(80m);  // March
    dto.MonthlySpend[6].Should().Be(300m); // July
    dto.BadgesEarnedCount.Should().Be(2);
    dto.BestBadge!.Key.Should().Be("savings_milestone");
    dto.TopCategories.Should().NotBeEmpty();
    dto.TopCategories.First().Name.Should().Be("DiningOut");
  }
}
