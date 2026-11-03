using Xunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.UnitTests.Services;

public class ReviewerEnrichmentTests : IDisposable
{
  private readonly BuoyAIDbContext _context;
  private int _reviewerId;

  public ReviewerEnrichmentTests()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;
    _context = new BuoyAIDbContext(options);

    var reviewer = new User
    {
      Username = "play-reviewer",
      Email = "play-reviewer@buoy-ai.com",
      PasswordHash = "x",
      Currency = "USD",
      SubscriptionTier = SubscriptionTier.Premium,
    };
    _context.Users.Add(reviewer);
    _context.SaveChanges();
    _reviewerId = reviewer.Id;
  }

  public void Dispose() => _context.Dispose();

  [Fact]
  public async Task Enrich_CreatesMissingStreaksAndBadges_WithoutTouchingExisting()
  {
    // The prod reviewer already has an under_budget streak — it must survive untouched.
    _context.Set<UserStreak>().Add(new UserStreak
    {
      UserId = _reviewerId,
      StreakType = "under_budget",
      CurrentCount = 42,
      BestCount = 42,
      LastIncrementDate = DateTime.UtcNow.Date,
      StreakStartDate = DateTime.UtcNow.Date.AddDays(-42),
    });
    await _context.SaveChangesAsync();

    await ReviewerAccountSeeder.EnrichReviewerAsync(_context, _reviewerId);

    var streaks = await _context.Set<UserStreak>()
      .Where(s => s.UserId == _reviewerId).ToListAsync();
    streaks.Should().HaveCount(5); // all five types present
    streaks.Single(s => s.StreakType == "under_budget").CurrentCount
      .Should().Be(42); // pre-existing row untouched
    streaks.Select(s => s.StreakType).Should()
      .Contain(new[] { "login", "categorization", "savings_goal" });

    var badges = await _context.Set<UserAchievement>()
      .Where(a => a.UserId == _reviewerId).ToListAsync();
    badges.Should().NotBeEmpty();
    badges.Should().OnlyContain(b => !b.IsNew); // no celebration spam
  }

  [Fact]
  public async Task Enrich_IsIdempotent_SecondRunAddsNothing()
  {
    await ReviewerAccountSeeder.EnrichReviewerAsync(_context, _reviewerId);
    var streakCount = await _context.Set<UserStreak>().CountAsync();
    var badgeCount = await _context.Set<UserAchievement>().CountAsync();

    await ReviewerAccountSeeder.EnrichReviewerAsync(_context, _reviewerId);

    (await _context.Set<UserStreak>().CountAsync()).Should().Be(streakCount);
    (await _context.Set<UserAchievement>().CountAsync()).Should().Be(badgeCount);
  }

  [Fact]
  public async Task Enrich_TopsUpStaleMonths_AndSkipsFreshOnes()
  {
    var checking = new Account
    {
      UserId = _reviewerId,
      PlaidAccountId = "acc-check",
      Name = "Checking",
      Type = AccountType.Checking,
      Currency = "USD",
      IsActive = true,
    };
    _context.Accounts.Add(checking);
    await _context.SaveChangesAsync();

    // Current month already has one transaction — must NOT be topped up.
    _context.Transactions.Add(new Transaction
    {
      AccountId = checking.Id,
      PlaidTransactionId = "existing-1",
      Amount = 10m,
      Type = TransactionType.Debit,
      Description = "COFFEE",
      MerchantName = "Cafe",
      CategoryId = (int)TransactionCategory.DiningOut,
      Date = DateTime.UtcNow.Date,
    });
    await _context.SaveChangesAsync();

    await ReviewerAccountSeeder.EnrichReviewerAsync(_context, _reviewerId);

    var now = DateTime.UtcNow;
    var thisStart = new DateTime(now.Year, now.Month, 1);
    var currentMonthCount = await _context.Transactions
      .CountAsync(t => t.AccountId == checking.Id && t.Date >= thisStart);
    currentMonthCount.Should().Be(1); // untouched

    var prevStart = thisStart.AddMonths(-1);
    var prevMonthCount = await _context.Transactions
      .CountAsync(t => t.AccountId == checking.Id && t.Date >= prevStart && t.Date < thisStart);
    prevMonthCount.Should().BeGreaterThan(10); // stale month replayed
  }
}
