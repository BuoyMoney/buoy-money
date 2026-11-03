using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.Infrastructure.Data;

/// <summary>
/// Seeds a dedicated "edge-case" QE fixture (issue #615 §0) — pathological data pre-loaded so QA
/// can probe validation, rendering and RBAC write-blocking without hand-crafting it each time.
/// Staging + Development only. Idempotent (skips if it already exists) and wrapped in one
/// transaction (a mid-way failure rolls back fully instead of leaving a partial fixture).
///
/// QE login: <c>edge-case@buoy-ai.com</c> / <c>BuoyDevLocal2025!</c> — a **ReadOnly** member of
/// "Edge Case Family". It sees edge-owner's pathological family data (negative / zero / huge
/// amounts, unicode+emoji and injection-looking strings, a very long description, a rollover cap
/// with no overflow savings goal) AND — being ReadOnly — is the account for probing write-blocking.
/// <c>edge-owner@buoy-ai.com</c> owns the data (so it was created through a writer, i.e. valid to
/// exist), and edge-case carries an odd custom avatar.
/// </summary>
public static class EdgeCaseAccountSeeder
{
  public const string OwnerEmail = "edge-owner@buoy-ai.com";
  public const string ReadOnlyEmail = "edge-case@buoy-ai.com";
  private const string Password = "BuoyDevLocal2025!";
  private static readonly DateTime Now = DateTime.UtcNow;

  // A real (1x1 transparent) PNG so the "custom avatar attached" render path is exercised. True
  // oversized/wrong-type uploads are rejected at the upload endpoint (512 KB + content-type gate),
  // so that validation is exercised through the UI, not seeded here.
  private static readonly byte[] AvatarPng = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

  public static async Task SeedAsync(BuoyAIDbContext context)
  {
    if (await context.Users.AnyAsync(u => u.Email == ReadOnlyEmail))
    {
      Console.WriteLine($"Edge-case account {ReadOnlyEmail} already exists, skipping edge-case seeding");
      return;
    }

    Console.WriteLine("=== Seeding QE edge-case account (pathological data) ===");

    // Whole seed in one transaction (context uses NpgsqlRetryingExecutionStrategy → run inside an
    // execution strategy), same pattern as ReviewerAccountSeeder.
    var strategy = context.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
      await using var transaction = await context.Database.BeginTransactionAsync();
      await SeedCoreAsync(context);
      await transaction.CommitAsync();
    });
  }

  private static async Task SeedCoreAsync(BuoyAIDbContext context)
  {
    var hash = AuthService.HashPassword(Password);

    var owner = new User
    {
      Username = "edge-owner",
      Email = OwnerEmail,
      PasswordHash = hash,
      FirstName = "Edge",
      LastName = "Owner",
      Currency = "USD",
      CreatedAt = Now.AddMonths(-3),
      UpdatedAt = Now,
      SubscriptionTier = SubscriptionTier.Premium,
      IsEmailVerified = true,
      EmailVerifiedAt = Now.AddMonths(-3)
    };
    var readOnly = new User
    {
      Username = "edge-case",
      Email = ReadOnlyEmail,
      PasswordHash = hash,
      FirstName = "Edge",
      LastName = "Case",
      Currency = "USD",
      CreatedAt = Now.AddMonths(-3),
      UpdatedAt = Now,
      SubscriptionTier = SubscriptionTier.PremiumInherited,
      IsEmailVerified = true,
      EmailVerifiedAt = Now.AddMonths(-3),
      AvatarImage = AvatarPng,
      AvatarContentType = "image/png",
      AvatarUpdatedAt = Now
    };
    context.Users.AddRange(owner, readOnly);
    await context.SaveChangesAsync();

    var family = new Family
    {
      Name = "Edge Case Family",
      CreatedByUserId = owner.Id,
      CreatedAt = Now.AddMonths(-3),
      UpdatedAt = Now
    };
    context.Families.Add(family);
    await context.SaveChangesAsync();

    owner.FamilyId = family.Id;
    readOnly.FamilyId = family.Id;
    context.FamilyMembers.AddRange(
      new FamilyMember { FamilyId = family.Id, UserId = owner.Id, Role = FamilyMemberRole.Owner, JoinedAt = Now.AddMonths(-3), IsActive = true },
      new FamilyMember { FamilyId = family.Id, UserId = readOnly.Id, Role = FamilyMemberRole.ReadOnly, JoinedAt = Now.AddMonths(-3), IsActive = true });
    await context.SaveChangesAsync();

    // Account with extreme balances (huge current, negative available).
    var account = new Account
    {
      UserId = owner.Id,
      PlaidAccountId = "manual-edge-checking",
      Name = "Edge Case Checking",
      OfficialName = "Edge Case Total Checking",
      Mask = "0000",
      Type = AccountType.Checking,
      Subtype = "checking",
      CurrentBalance = 999999999.99m,
      AvailableBalance = -4200.50m,
      Currency = "USD",
      IsActive = true,
      CreatedAt = Now.AddMonths(-3),
      UpdatedAt = Now
    };
    context.Accounts.Add(account);
    await context.SaveChangesAsync();

    // Pathological transactions: extreme amount, zero amount, unicode+emoji merchant,
    // injection-looking strings, and a maximally long (500-char) description.
    Transaction Txn(decimal amount, string description, string merchant, TransactionCategory cat, DateTime date) =>
      new Transaction
      {
        AccountId = account.Id,
        PlaidTransactionId = $"manual-edge-{Guid.NewGuid():N}",
        Amount = amount,
        Type = TransactionType.Debit,
        Description = description,
        MerchantName = merchant,
        CategoryId = (int)cat,
        Date = date,
        IsPending = false,
        IsUserCategorized = false,
        CategorySource = "Seeded",
        CreatedAt = date,
        UpdatedAt = date
      };

    context.Transactions.AddRange(
      Txn(999999999.99m, "EXTREME AMOUNT", "Mega Corp", TransactionCategory.Shopping, Now.AddDays(-1)),
      Txn(0m, "ZERO AMOUNT", "Zero LLC", TransactionCategory.Other, Now.AddDays(-2)),
      Txn(12.34m, "Unicode + emoji merchant", "🍕 Café Münchën 日本語 ☕", TransactionCategory.DiningOut, Now.AddDays(-3)),
      Txn(56.78m, "Injection-looking strings", "'; DROP TABLE \"Users\";-- <script>alert('x')</script>", TransactionCategory.Groceries, Now.AddDays(-4)),
      Txn(99.99m, new string('A', 480), "Very Long Description Co", TransactionCategory.Entertainment, Now.AddDays(-5)));
    await context.SaveChangesAsync();

    // Pathological FAMILY budget (visible to the ReadOnly member) with invalid-looking allocations.
    var start = new DateTime(Now.Year, Now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    var end = new DateTime(Now.Year, Now.Month, DateTime.DaysInMonth(Now.Year, Now.Month), 23, 59, 59, DateTimeKind.Utc);
    var budget = new Budget
    {
      UserId = owner.Id,
      FamilyId = family.Id,
      Name = "Edge Case Budget",
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = start,
      CurrentPeriodEnd = end,
      LastReset = start,
      AutoReset = true,
      TotalIncome = 0m,
      TotalExpenses = 0m,
      IsActive = true,
      Type = BudgetType.Family,
      IsPrimary = true,
      RolloverMode = ChildBudgetRolloverMode.RolloverToSelf,
      CreatedAt = Now.AddMonths(-3),
      UpdatedAt = Now
    };
    context.Budgets.Add(budget);
    await context.SaveChangesAsync();

    BudgetCategory Cat(TransactionCategory cat, decimal allocated, int sort, decimal? maxRolloverMultiplier = null) =>
      new BudgetCategory
      {
        BudgetId = budget.Id,
        CategoryId = (int)cat,
        IsIncome = false,
        AllocatedAmount = allocated,
        SortOrder = sort,
        AllowRollover = true,
        RolloverBalance = 0m,
        IsCountUpEnabled = false,
        DailyAllowance = 0m,
        EarnedToDate = 0m,
        LastAllowanceCalculation = Now,
        // Deliberately inconsistent: a rollover cap with NO overflow savings goal — app-level
        // validation says this is "required", the DB does not enforce it, so it's a real edge state.
        MaxRolloverMultiplier = maxRolloverMultiplier,
        CreatedAt = Now,
        UpdatedAt = Now
      };

    context.BudgetCategories.AddRange(
      Cat(TransactionCategory.Shopping, -500.00m, 0),                    // negative allocation
      Cat(TransactionCategory.Groceries, 0m, 1),                         // zero allocation
      Cat(TransactionCategory.DiningOut, 999999999.99m, 2),              // huge allocation
      Cat(TransactionCategory.Entertainment, 100.00m, 3, maxRolloverMultiplier: 2.0m)); // cap set, no overflow goal
    await context.SaveChangesAsync();

    Console.WriteLine($"Seeded edge-case fixture: {ReadOnlyEmail} (ReadOnly) + {OwnerEmail} (owner) with pathological data");
  }
}
