using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.DTOs.Gamification;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.Infrastructure.Data;

/// <summary>
/// Seeds the per-clip demo accounts used to film short-form marketing videos
/// One account per clip so features
/// never fight over the same data — re-running auto-categorize can't drain the ticker
/// account's backlog, badge-hunting can't pollute the Wrapped month.
///
/// Development and Staging (filming runs on a real device against staging; never
/// Production). Wipes and reseeds its own accounts on every start, so "reset the set" =
/// restart the API (staging: restart the API, which also refreshes
/// all relative dates). All accounts are Premium via plain User fields — no Stripe
/// objects, so staging's shared live Stripe account is never touched. Password
/// BuoyDevLocal2025! (BUOYAI_TEST_PASSWORD).
///
/// - demo-ticker@buoy-ai.com        (Penny Upton)   : 6 count-up categories, spread allowances
/// - demo-categorize@buoy-ai.com    (Kat Sorrell)   : ~100 uncategorized transactions
/// - demo-subscriptions@buoy-ai.com (Ren Ewell)     : detection patterns + calendar spread + stale sub
/// - demo-badges@buoy-ai.com        (Goldie Laurels): data primed to fire a multi-unlock chain
/// - demo-oops@buoy-ai.com          (Rue Overton)   : Time Traveler / Selective Amnesia states
/// - demo-quiz@buoy-ai.com          (Archie Tyler)  : perpetually fresh, quiz never taken
/// - demo-ai-chat@buoy-ai.com       (Sage Askins)   : 6 months of data + AI memories
/// - demo-wrapped@buoy-ai.com       (Wren Summers)  : one immaculate closed month
/// - demo-nospend@buoy-ai.com       (Nova Spendlow) : streak + freeze + challenge 1 step from done
/// - demo-family-owner@buoy-ai.com  (Dana Marlowe)  : family owner (admin) of "The Marlowes"
/// - demo-family-viewer@buoy-ai.com (Theo Marlowe)  : read-only member of the same family
/// </summary>
public static class DemoAccountSeeder
{
  private const string Password = "BuoyDevLocal2025!";
  private static readonly DateTime Now = DateTime.UtcNow;

  private static readonly string[] DemoEmails =
  {
    "demo-ticker@buoy-ai.com",
    "demo-categorize@buoy-ai.com",
    "demo-subscriptions@buoy-ai.com",
    "demo-badges@buoy-ai.com",
    "demo-oops@buoy-ai.com",
    "demo-quiz@buoy-ai.com",
    "demo-ai-chat@buoy-ai.com",
    "demo-wrapped@buoy-ai.com",
    "demo-nospend@buoy-ai.com",
    "demo-family-owner@buoy-ai.com",
    "demo-family-viewer@buoy-ai.com"
  };

  public static async Task SeedAsync(BuoyAIDbContext context)
  {
    Console.WriteLine("=== Seeding demo (filming) accounts ===");

    // Self-contained wipe: TestDataSeeder's dev wipe usually removes these first (they match
    // its @buoy-ai.com pattern), but don't depend on ordering — reseed from a clean slate.
    var existing = await context.Users.Where(u => DemoEmails.Contains(u.Email)).ToListAsync();
    if (existing.Any())
    {
      var existingIds = existing.Select(u => u.Id).ToList();
      var oldSubs = await context.UserSubscriptions.Where(s => existingIds.Contains(s.UserId)).ToListAsync();
      context.UserSubscriptions.RemoveRange(oldSubs);
      // A family created by a demo user blocks deleting that user (no delete rule on
      // FK_Families_Users_CreatedByUserId); remove it first.
      var ownedFamilies = await context.Families.Where(f => existingIds.Contains(f.CreatedByUserId)).ToListAsync();
      context.Families.RemoveRange(ownedFamilies);
      await context.SaveChangesAsync(); // separate save: family and creator reference each other
      context.Users.RemoveRange(existing);
      await context.SaveChangesAsync();
    }

    var strategy = context.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
      await using var transaction = await context.Database.BeginTransactionAsync();
      await SeedCoreAsync(context);
      await SeedPremiumSubscriptionsAsync(context);
      await transaction.CommitAsync();
    });

    Console.WriteLine("=== Demo account seeding complete ===");
  }

  private static async Task SeedCoreAsync(BuoyAIDbContext context)
  {
    await SeedPennyTickerAsync(context);
    await SeedKatCategorizeAsync(context);
    await SeedRenSubscriptionsAsync(context);
    await SeedGoldieBadgesAsync(context);
    await SeedRueOopsAsync(context);
    await SeedArchieQuizAsync(context);
    await SeedSageAiChatAsync(context);
    await SeedWrenWrappedAsync(context);
    await SeedNovaNoSpendAsync(context);
    await SeedMarloweFamilyAsync(context);
  }

  /// <summary>
  /// Tier is resolved from the projection (an active UserSubscription row or an active
  /// trial), never from User.SubscriptionTier alone, so the Premium flag on the demo users
  /// was decorative: on staging every demo account resolved to "no tier" and the Premium
  /// gates (AI chat) answered 403 while filming. One Active Premium row per demo user, with
  /// no Stripe ids, makes them Premium for real without touching the shared live Stripe
  /// account. Billing code only calls Stripe on a "no tier" answer, which these never give.
  /// </summary>
  private static async Task SeedPremiumSubscriptionsAsync(BuoyAIDbContext context)
  {
    var demoUsers = await context.Users.Where(u => DemoEmails.Contains(u.Email)).ToListAsync();
    context.UserSubscriptions.AddRange(demoUsers.Select(u => new UserSubscription
    {
      UserId = u.Id,
      Tier = SubscriptionTier.Premium,
      BillingCycle = SubscriptionBillingCycle.Monthly,
      Amount = 0m,
      StartDate = Now.AddMonths(-6),
      NextBillingDate = Now.AddDays(30),
      Status = UserSubscriptionStatus.Active,
      CreatedAt = Now.AddMonths(-6),
      UpdatedAt = Now
    }));
    await context.SaveChangesAsync();
    Console.WriteLine($"  Seeded {demoUsers.Count} demo Premium subscriptions (no Stripe objects)");
  }

  // ── Shared helpers ────────────────────────────────────────────────────────

  private static User CreateDemoUser(string username, string email, string firstName, string lastName)
  {
    return new User
    {
      Username = username,
      PasswordHash = AuthService.HashPassword(Password),
      Email = email,
      FirstName = firstName,
      LastName = lastName,
      Currency = "USD",
      CreatedAt = Now.AddMonths(-6),
      UpdatedAt = Now,
      SubscriptionTier = SubscriptionTier.Premium,
      SubscriptionStartDate = Now.AddMonths(-6),
      SubscriptionEndDate = Now.AddMonths(6),
      HasUsedTrial = true,
      IsTrialActive = false,
      IsEmailVerified = true,
      EmailVerifiedAt = Now.AddMonths(-6),
      // The personas are users who turned AI features on. Scenes film the features (chat
      // replies, auto-categorize), not the consent sheet, and since consent became explicit
      // and versioned (#1045) an unconsented demo account gets no AI at all: chat never
      // answers and the Auto-Categorize button is replaced by the enable card.
      AiConsentGrantedAt = Now.AddMonths(-6),
      AiConsentVersion = AiConsent.CurrentVersion
    };
  }

  private static Account CreateChecking(int userId, string slug, decimal balance)
  {
    return new Account
    {
      UserId = userId,
      PlaidAccountId = $"manual-{slug}-checking",
      Name = "Checking",
      OfficialName = "Everyday Checking",
      Mask = "1010",
      Type = AccountType.Checking,
      Subtype = "checking",
      CurrentBalance = balance,
      AvailableBalance = balance,
      Currency = "USD",
      IsActive = true,
      CreatedAt = Now.AddMonths(-6),
      UpdatedAt = Now
    };
  }

  private static Transaction CreateTransaction(
      int accountId,
      decimal amount,
      TransactionType type,
      string description,
      string merchantName,
      int categoryId,
      DateTime date)
  {
    return new Transaction
    {
      AccountId = accountId,
      PlaidTransactionId = $"demo-{Guid.NewGuid():N}",
      Amount = Math.Abs(amount),
      Type = type,
      Description = description,
      MerchantName = merchantName,
      CategoryId = categoryId,
      Date = date,
      IsPending = false,
      IsUserCategorized = false,
      CategorySource = "Seeded",
      CreatedAt = date,
      UpdatedAt = date
    };
  }

  private static Transaction Spend(int accountId, decimal amount, string description,
      string merchant, TransactionCategory category, DateTime date) =>
    CreateTransaction(accountId, amount, TransactionType.Debit, description, merchant, (int)category, date);

  private static Transaction Income(int accountId, decimal amount, string description,
      string merchant, DateTime date) =>
    CreateTransaction(accountId, amount, TransactionType.Credit, description, merchant,
        (int)TransactionCategory.Salary, date);

  private static (DateTime start, DateTime end) CurrentMonthPeriod()
  {
    var start = new DateTime(Now.Year, Now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    var end = new DateTime(Now.Year, Now.Month, DateTime.DaysInMonth(Now.Year, Now.Month), 23, 59, 59, DateTimeKind.Utc);
    return (start, end);
  }

  private static Budget CreateMonthlyBudget(int userId, string name)
  {
    var (start, end) = CurrentMonthPeriod();
    return new Budget
    {
      UserId = userId,
      Name = name,
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = start,
      CurrentPeriodEnd = end,
      LastReset = start,
      AutoReset = true,
      IsActive = true,
      Type = BudgetType.Personal,
      IsPrimary = true,
      CreatedAt = Now.AddMonths(-5),
      UpdatedAt = Now
    };
  }

  private static BudgetCategory Category(int budgetId, TransactionCategory category, bool isIncome,
      decimal allocated, int sortOrder)
  {
    return new BudgetCategory
    {
      BudgetId = budgetId,
      CategoryId = (int)category,
      IsIncome = isIncome,
      AllocatedAmount = allocated,
      SortOrder = sortOrder,
      LastAllowanceCalculation = Now,
      CreatedAt = Now.AddMonths(-5),
      UpdatedAt = Now
    };
  }

  // ── Penny Upton — live count-up ticker ───────────────────────────────────
  // 6 count-up categories with spread daily allowances so cards visibly tick at
  // different speeds and the aggregate ticks fastest. Spending kept light so every
  // balance is green on camera.
  private static async Task SeedPennyTickerAsync(BuoyAIDbContext context)
  {
    var user = CreateDemoUser("demo-ticker", "demo-ticker@buoy-ai.com", "Penny", "Upton");
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var checking = CreateChecking(user.Id, "ticker", 6400m);
    context.Accounts.Add(checking);
    await context.SaveChangesAsync();

    var budget = CreateMonthlyBudget(user.Id, "Penny's Monthly Budget");
    context.Budgets.Add(budget);
    await context.SaveChangesAsync();

    var daysInMonth = DateTime.DaysInMonth(Now.Year, Now.Month);
    // Spread tuned for camera: Rent sprints (~9.3 fourth-decimal flips/sec),
    // PersonalCare crawls (one flip per ~1.3s), and the header aggregate
    // ($177.50/day) flies at ~20 flips/sec.
    var countUps = new (TransactionCategory cat, decimal daily)[]
    {
      (TransactionCategory.Rent, 80.00m),
      (TransactionCategory.Groceries, 36.00m),
      (TransactionCategory.DiningOut, 24.00m),
      (TransactionCategory.Shopping, 18.00m),
      (TransactionCategory.Entertainment, 13.00m),
      (TransactionCategory.PersonalCare, 6.50m)
    };

    var categories = new List<BudgetCategory>
    {
      Category(budget.Id, TransactionCategory.Salary, true, 6200m, 0)
    };
    var sort = 1;
    foreach (var (cat, daily) in countUps)
    {
      var c = Category(budget.Id, cat, false, daily * daysInMonth, sort++);
      c.IsCountUpEnabled = true;
      c.DailyAllowance = daily;
      categories.Add(c);
    }
    context.BudgetCategories.AddRange(categories);
    await context.SaveChangesAsync();

    // Light spend — small enough to stay green even early in the month.
    context.Transactions.AddRange(
      Income(checking.Id, 3100m, "PAYROLL DEPOSIT", "Harbor Labs", Now.AddDays(-1)),
      Income(checking.Id, 3100m, "PAYROLL DEPOSIT", "Harbor Labs", Now.AddDays(-15)),
      Spend(checking.Id, 14.62m, "TRADER JOES", "Trader Joe's", TransactionCategory.Groceries, Now.AddDays(-1)),
      Spend(checking.Id, 9.80m, "CHIPOTLE", "Chipotle", TransactionCategory.DiningOut, Now.AddDays(-2)),
      Spend(checking.Id, 7.99m, "TARGET", "Target", TransactionCategory.Shopping, Now.AddDays(-1)),
      Spend(checking.Id, 4.99m, "AMC THEATRES", "AMC", TransactionCategory.Entertainment, Now.AddDays(-3)),
      Spend(checking.Id, 2.50m, "DOLLAR SHAVE CLUB", "Dollar Shave Club", TransactionCategory.PersonalCare, Now.AddDays(-2)));
    await context.SaveChangesAsync();
    Console.WriteLine("  Seeded Penny Upton (demo-ticker) — 6 count-up categories");
  }

  // ── Kat Sorrell — bulk AI auto-categorize ─────────────────────────────────
  // ~100 uncategorized transactions with recognizable merchants so the "needs
  // review" banner shows a big number and the progress bar has work to do.
  private static async Task SeedKatCategorizeAsync(BuoyAIDbContext context)
  {
    var user = CreateDemoUser("demo-categorize", "demo-categorize@buoy-ai.com", "Kat", "Sorrell");
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var checking = CreateChecking(user.Id, "categorize", 4100m);
    context.Accounts.Add(checking);
    await context.SaveChangesAsync();

    var budget = CreateMonthlyBudget(user.Id, "Kat's Monthly Budget");
    context.Budgets.Add(budget);
    await context.SaveChangesAsync();
    context.BudgetCategories.AddRange(
      Category(budget.Id, TransactionCategory.Salary, true, 4800m, 0),
      Category(budget.Id, TransactionCategory.Groceries, false, 550m, 1),
      Category(budget.Id, TransactionCategory.DiningOut, false, 300m, 2),
      Category(budget.Id, TransactionCategory.Transportation, false, 250m, 3),
      Category(budget.Id, TransactionCategory.Shopping, false, 350m, 4));
    await context.SaveChangesAsync();

    var merchants = new (string desc, string merchant, decimal min, decimal max)[]
    {
      ("STARBUCKS STORE 8841", "Starbucks", 4.85m, 12.40m),
      ("SHELL OIL 5731", "Shell", 28m, 62m),
      ("KROGER #212", "Kroger", 22m, 145m),
      ("AMAZON MKTPL*2K4XY", "Amazon", 9m, 89m),
      ("CHIPOTLE 1188", "Chipotle", 10m, 27m),
      ("TARGET T-1042", "Target", 14m, 96m),
      ("UBER TRIP HELP.UBER.COM", "Uber", 8m, 34m),
      ("CVS/PHARMACY #04512", "CVS", 6m, 42m),
      ("MCDONALD'S F13782", "McDonald's", 5m, 16m),
      ("HOME DEPOT #6712", "Home Depot", 12m, 118m),
      ("WALGREENS #7455", "Walgreens", 5m, 38m),
      ("PANERA BREAD #601444", "Panera Bread", 9m, 22m)
    };

    var random = new Random(42); // Fixed seed for reproducible data
    var transactions = new List<Transaction>();
    // Auto-Categorize is window-bound (this month and last, see
    // docs/ai_categorization_pipeline.md), so Kat's uncategorized rows must all sit inside
    // that window or the demo's "100 can be auto-categorized" banner shrinks to whatever
    // slice happens to fall after the 1st of last month. Spread across the whole window
    // (never fewer than 14 days so the 1st of a month still gets a spread).
    var windowStart = new DateTime(Now.Year, Now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
    var windowDays = Math.Max(14, (int)(Now - windowStart).TotalDays);
    for (var i = 0; i < 100; i++)
    {
      var (desc, merchant, min, max) = merchants[i % merchants.Length];
      var amount = Math.Round(min + (decimal)random.NextDouble() * (max - min), 2);
      var date = Now.AddDays(-random.Next(1, windowDays)).AddHours(-random.Next(0, 12));
      var tx = CreateTransaction(checking.Id, amount, TransactionType.Debit, desc, merchant,
          (int)TransactionCategory.Uncategorized, date);
      // Real Plaid ingest flags uncategorized transactions for review; without this the
      // needs-review banner (and its Auto-Categorize button) never renders for Kat.
      tx.NeedsReview = true;
      transactions.Add(tx);
    }
    // Categorized income so the budget view isn't empty.
    transactions.Add(Income(checking.Id, 2400m, "PAYROLL DEPOSIT", "Bluewater Design", Now.AddDays(-4)));
    transactions.Add(Income(checking.Id, 2400m, "PAYROLL DEPOSIT", "Bluewater Design", Now.AddDays(-18)));
    context.Transactions.AddRange(transactions);
    await context.SaveChangesAsync();
    Console.WriteLine("  Seeded Kat Sorrell (demo-categorize) — 100 uncategorized transactions");
  }

  // ── Ren Ewell — subscription detection + renewal calendar ────────────────
  // Detection-pattern merchants are deliberately DISTINCT from the confirmed
  // subscriptions, so the detection sheet finds fresh candidates while the
  // calendar is already populated. One confirmed sub stopped billing months ago
  // to trigger the cancelled-review flow.
  private static async Task SeedRenSubscriptionsAsync(BuoyAIDbContext context)
  {
    var user = CreateDemoUser("demo-subscriptions", "demo-subscriptions@buoy-ai.com", "Ren", "Ewell");
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var checking = CreateChecking(user.Id, "subscriptions", 5300m);
    context.Accounts.Add(checking);
    await context.SaveChangesAsync();

    var transactions = new List<Transaction>();

    // Monthly patterns anchored to a fixed day of month (same technique as
    // TestDataSeeder's detection patterns).
    void MonthlyPattern(string desc, string merchant, decimal amount, int dayOfMonth, int months,
        TransactionCategory cat = TransactionCategory.Subscriptions, int[]? drift = null)
    {
      for (var i = 0; i < months; i++)
      {
        var date = Now.AddMonths(-i).Date.AddDays(-(Now.Day - dayOfMonth));
        if (date > Now) date = date.AddMonths(-1);
        if (drift != null) date = date.AddDays(drift[i % drift.Length]);
        transactions.Add(Spend(checking.Id, amount, desc, merchant, cat, date));
      }
    }

    MonthlyPattern("NETFLIX.COM", "Netflix", 15.49m, 3, 8);
    MonthlyPattern("SPOTIFY USA", "Spotify", 11.99m, 8, 7, drift: new[] { 0, 1, -1, 2, 0, -2, 1 });
    MonthlyPattern("ADOBE CREATIVE CLOUD", "Adobe", 54.99m, 12, 6);
    MonthlyPattern("PLANET FIT CLUB FEES", "Planet Fitness", 24.99m, 17, 9);
    MonthlyPattern("APPLE.COM/BILL ICLOUD", "Apple iCloud", 2.99m, 20, 10);
    MonthlyPattern("AUDIBLE*MEMBERSHIP", "Audible", 14.95m, 24, 6);
    MonthlyPattern("YOUTUBE PREMIUM", "YouTube Premium", 13.99m, 27, 6);

    // Variable-amount utility (medium confidence).
    var verizonAmounts = new[] { 89.99m, 94.50m, 87.25m, 102.30m, 91.45m, 88.00m, 99.99m, 95.50m };
    for (var i = 0; i < verizonAmounts.Length; i++)
    {
      var date = Now.AddMonths(-i).Date.AddDays(-(Now.Day - 22));
      if (date > Now) date = date.AddMonths(-1);
      transactions.Add(Spend(checking.Id, verizonAmounts[i], "VERIZON WIRELESS PMT", "Verizon",
          TransactionCategory.Utilities, date));
    }

    // Weekly + bi-weekly + quarterly cadences.
    for (var i = 0; i < 12; i++)
      transactions.Add(Spend(checking.Id, 35.00m, "IRON ANCHOR CROSSFIT", "Iron Anchor CrossFit",
          TransactionCategory.PersonalCare, Now.Date.AddDays(-7.0 * i - 2.0)));
    for (var i = 0; i < 8; i++)
      transactions.Add(Spend(checking.Id, 49.00m, "CLASSPASS INC", "ClassPass",
          TransactionCategory.PersonalCare, Now.Date.AddDays(-14.0 * i - 5.0)));
    for (var i = 0; i < 4; i++)
    {
      var date = Now.AddMonths(-3 * i).Date.AddDays(-(Now.Day - 10));
      if (date > Now) date = date.AddMonths(-3);
      transactions.Add(Spend(checking.Id, 59.99m, "LINKEDIN PREMIUM", "LinkedIn",
          TransactionCategory.Subscriptions, date));
    }

    // Stopped billing ~75 days ago — pairs with the stale confirmed sub below so
    // the cancelled-review sheet has a real timeline to show.
    for (var i = 0; i < 10; i++)
      transactions.Add(Spend(checking.Id, 71.94m, "HELLOFRESH", "HelloFresh",
          TransactionCategory.Groceries, Now.Date.AddDays(-75.0 - 7.0 * i)));

    // Everyday noise so the transaction list looks real (and detection has false-positive bait).
    var random = new Random(7);
    var noise = new (string desc, string merchant, TransactionCategory cat, decimal min, decimal max)[]
    {
      ("WHOLE FOODS MKT", "Whole Foods", TransactionCategory.Groceries, 30m, 160m),
      ("SHELL OIL", "Shell", TransactionCategory.Transportation, 28m, 58m),
      ("AMAZON MKTPL", "Amazon", TransactionCategory.Shopping, 12m, 85m)
    };
    for (var i = 0; i < 24; i++)
    {
      var (desc, merchant, cat, min, max) = noise[i % noise.Length];
      var amount = Math.Round(min + (decimal)random.NextDouble() * (max - min), 2);
      transactions.Add(Spend(checking.Id, amount, desc, merchant, cat, Now.AddDays(-random.Next(1, 80))));
    }
    transactions.Add(Income(checking.Id, 3100m, "PAYROLL DEPOSIT", "Northgate Media", Now.AddDays(-3)));
    transactions.Add(Income(checking.Id, 3100m, "PAYROLL DEPOSIT", "Northgate Media", Now.AddDays(-17)));
    context.Transactions.AddRange(transactions);
    await context.SaveChangesAsync();

    // Confirmed subscriptions — renewal dates spread across the month so the
    // calendar view fills, plus the stale HelloFresh for cancelled review.
    Subscription Confirmed(string name, string merchant, decimal amount, int dueInDays,
        SubscriptionCategory category) => new()
    {
      UserId = user.Id,
      Name = name,
      MerchantName = merchant,
      Amount = amount,
      Frequency = SubscriptionFrequency.Monthly,
      NextDueDate = Now.AddDays(dueInDays),
      LastBilledDate = Now.AddDays(dueInDays - 30),
      Status = SubscriptionStatus.Active,
      Category = category,
      IsDetected = true,
      CreatedAt = Now.AddMonths(-8),
      UpdatedAt = Now
    };

    var stale = Confirmed("HelloFresh", "HelloFresh", 71.94m, -68, SubscriptionCategory.Other);
    stale.LastBilledDate = Now.AddDays(-75);
    context.Subscriptions.AddRange(
      Confirmed("Disney+", "Disney+", 13.99m, 5, SubscriptionCategory.Streaming),
      Confirmed("Hulu", "Hulu", 17.99m, 11, SubscriptionCategory.Streaming),
      Confirmed("NY Times", "NYTimes", 17.00m, 18, SubscriptionCategory.News),
      Confirmed("Dropbox", "Dropbox", 11.99m, 25, SubscriptionCategory.Software),
      stale);
    await context.SaveChangesAsync();
    Console.WriteLine("  Seeded Ren Ewell (demo-subscriptions) — 12 detectable patterns + calendar spread");
  }

  // ── Goldie Laurels — achievement unlock chain ─────────────────────────────
  // Only a few badges pre-earned; the data states below are primed so the
  // achievements page's check awards a chain of NEW badges on first open
  // (goal completed +25% over, $5k+ total saved, best streak 34d).
  private static async Task SeedGoldieBadgesAsync(BuoyAIDbContext context)
  {
    var user = CreateDemoUser("demo-badges", "demo-badges@buoy-ai.com", "Goldie", "Laurels");
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var checking = CreateChecking(user.Id, "badges", 7200m);
    context.Accounts.Add(checking);
    await context.SaveChangesAsync();

    var budget = CreateMonthlyBudget(user.Id, "Goldie's Monthly Budget");
    context.Budgets.Add(budget);
    await context.SaveChangesAsync();
    context.BudgetCategories.AddRange(
      Category(budget.Id, TransactionCategory.Salary, true, 5600m, 0),
      Category(budget.Id, TransactionCategory.Groceries, false, 600m, 1),
      Category(budget.Id, TransactionCategory.DiningOut, false, 280m, 2));
    await context.SaveChangesAsync();

    context.Transactions.AddRange(
      Income(checking.Id, 2800m, "PAYROLL DEPOSIT", "Lighthouse Co", Now.AddDays(-2)),
      Spend(checking.Id, 96.40m, "KROGER #212", "Kroger", TransactionCategory.Groceries, Now.AddDays(-3)),
      Spend(checking.Id, 41.25m, "OLIVE GARDEN", "Olive Garden", TransactionCategory.DiningOut, Now.AddDays(-5)));

    // Completed 30% past target → GoalAchiever + Overachiever; totals cross the
    // $1k and $5k milestones; second goal past 50% → HalfwayThere.
    context.SavingsGoals.AddRange(
      new SavingsGoal
      {
        UserId = user.Id,
        Type = SavingsGoalType.Personal,
        Name = "Emergency Fund",
        Description = "3 months of expenses",
        TargetAmount = 2000m,
        CurrentAmount = 2600m,
        AllocationPercentage = 10m,
        TargetDate = Now.AddMonths(-1),
        IconName = "shield",
        ColorTheme = "danger",
        IsActive = true,
        IsCompleted = true,
        CreatedAt = Now.AddMonths(-6),
        UpdatedAt = Now
      },
      new SavingsGoal
      {
        UserId = user.Id,
        Type = SavingsGoalType.Personal,
        Name = "New Camera",
        Description = "Filming gear",
        TargetAmount = 5000m,
        CurrentAmount = 2600m,
        AllocationPercentage = 10m,
        TargetDate = Now.AddMonths(8),
        IconName = "camera",
        ColorTheme = "info",
        IsActive = true,
        IsCompleted = false,
        CreatedAt = Now.AddMonths(-4),
        UpdatedAt = Now
      });

    // Best streak 34d → WeekStreak + MonthStreak fire on check.
    context.Set<UserStreak>().Add(new UserStreak
    {
      UserId = user.Id,
      StreakType = "under_budget",
      CurrentCount = 9,
      BestCount = 34,
      LastIncrementDate = Now.Date.AddDays(-1),
      StreakStartDate = Now.Date.AddDays(-9),
      StreakFreezes = 2,
      CreatedAt = Now.AddMonths(-3),
      UpdatedAt = Now
    });

    // A few already-earned badges (IsNew=false) so the grid shows earned tiers
    // next to locked mystery discs before the new chain fires.
    UserAchievement Earned(string key, DateTime when)
    {
      var def = AchievementDefinitions.All[key];
      return new UserAchievement
      {
        UserId = user.Id,
        AchievementKey = key,
        Title = def.Title,
        Description = def.Description,
        Icon = def.Icon,
        Category = def.Category,
        Points = def.Points,
        EarnedAt = when,
        IsNew = false
      };
    }
    context.Set<UserAchievement>().AddRange(
      Earned(AchievementDefinitions.FirstBudget, Now.AddMonths(-5)),
      Earned(AchievementDefinitions.FirstSavingsGoal, Now.AddMonths(-5)),
      Earned(AchievementDefinitions.QuizComplete, Now.AddMonths(-5)),
      Earned(AchievementDefinitions.Connector, Now.AddMonths(-2)));
    await context.SaveChangesAsync();
    Console.WriteLine("  Seeded Goldie Laurels (demo-badges) — primed for a multi-unlock chain");
  }

  // ── Rue Overton — oops badges + rollover honesty bar ──────────────────────
  // Groceries carries rollover debt bigger than the whole allocation (Time
  // Traveler state); Dining carries a moderate debt staged for an on-camera
  // reset (Selective Amnesia).
  private static async Task SeedRueOopsAsync(BuoyAIDbContext context)
  {
    var user = CreateDemoUser("demo-oops", "demo-oops@buoy-ai.com", "Rue", "Overton");
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var checking = CreateChecking(user.Id, "oops", 1850m);
    context.Accounts.Add(checking);
    await context.SaveChangesAsync();

    var budget = CreateMonthlyBudget(user.Id, "Rue's Monthly Budget");
    context.Budgets.Add(budget);
    await context.SaveChangesAsync();

    var groceries = Category(budget.Id, TransactionCategory.Groceries, false, 400m, 1);
    groceries.AllowRollover = true;
    groceries.RolloverBalance = -450m; // debt exceeds the allocation — Time Traveler
    var dining = Category(budget.Id, TransactionCategory.DiningOut, false, 250m, 2);
    dining.AllowRollover = true;
    dining.RolloverBalance = -120m; // staged for the on-camera debt reset
    context.BudgetCategories.AddRange(
      Category(budget.Id, TransactionCategory.Salary, true, 3900m, 0),
      groceries,
      dining,
      Category(budget.Id, TransactionCategory.Utilities, false, 180m, 3));
    await context.SaveChangesAsync();

    context.Transactions.AddRange(
      Income(checking.Id, 1950m, "PAYROLL DEPOSIT", "Driftwood Cafe", Now.AddDays(-2)),
      Income(checking.Id, 1950m, "PAYROLL DEPOSIT", "Driftwood Cafe", Now.AddDays(-16)),
      Spend(checking.Id, 132.18m, "KROGER #212", "Kroger", TransactionCategory.Groceries, Now.AddDays(-4)),
      Spend(checking.Id, 87.31m, "COSTCO WHOLESALE", "Costco", TransactionCategory.Groceries, Now.AddDays(-9)),
      Spend(checking.Id, 64.90m, "TEXAS ROADHOUSE", "Texas Roadhouse", TransactionCategory.DiningOut, Now.AddDays(-3)),
      Spend(checking.Id, 118.00m, "CITY UTILITIES", "City Utilities", TransactionCategory.Utilities, Now.AddDays(-6)));
    await context.SaveChangesAsync();
    Console.WriteLine("  Seeded Rue Overton (demo-oops) — Time Traveler + reset-ready debt");
  }

  // ── Archie Tyler — onboarding quiz ────────────────────────────────────────
  // Perpetually fresh: no accounts, no budget, quiz never taken, so the full
  // onboarding flow can be filmed repeatedly (reset = API restart).
  private static async Task SeedArchieQuizAsync(BuoyAIDbContext context)
  {
    var user = CreateDemoUser("demo-quiz", "demo-quiz@buoy-ai.com", "Archie", "Tyler");
    user.CreatedAt = Now.AddDays(-1);
    context.Users.Add(user);
    await context.SaveChangesAsync();
    Console.WriteLine("  Seeded Archie Tyler (demo-quiz) — fresh account, quiz untaken");
  }

  // ── Sage Askins — AI chat with tool calls + memories ─────────────────────
  // Six months of varied spending so "Analyzed Spending" answers are meaty,
  // plus pre-stored AI memories to show recall on camera.
  private static async Task SeedSageAiChatAsync(BuoyAIDbContext context)
  {
    var user = CreateDemoUser("demo-ai-chat", "demo-ai-chat@buoy-ai.com", "Sage", "Askins");
    user.AiMemoryEnabled = true;
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var checking = CreateChecking(user.Id, "ai-chat", 9100m);
    var savings = new Account
    {
      UserId = user.Id,
      PlaidAccountId = "manual-ai-chat-savings",
      Name = "High-Yield Savings",
      OfficialName = "Harbor Savings",
      Mask = "2020",
      Type = AccountType.Savings,
      Subtype = "savings",
      CurrentBalance = 22000m,
      AvailableBalance = 22000m,
      Currency = "USD",
      IsActive = true,
      CreatedAt = Now.AddMonths(-6),
      UpdatedAt = Now
    };
    context.Accounts.AddRange(checking, savings);
    await context.SaveChangesAsync();

    var budget = CreateMonthlyBudget(user.Id, "Sage's Monthly Budget");
    context.Budgets.Add(budget);
    await context.SaveChangesAsync();
    context.BudgetCategories.AddRange(
      Category(budget.Id, TransactionCategory.Salary, true, 8400m, 0),
      Category(budget.Id, TransactionCategory.Rent, false, 1900m, 1),
      Category(budget.Id, TransactionCategory.Groceries, false, 700m, 2),
      Category(budget.Id, TransactionCategory.DiningOut, false, 450m, 3),
      Category(budget.Id, TransactionCategory.Transportation, false, 300m, 4),
      Category(budget.Id, TransactionCategory.Subscriptions, false, 120m, 5),
      Category(budget.Id, TransactionCategory.Shopping, false, 400m, 6),
      Category(budget.Id, TransactionCategory.Utilities, false, 260m, 7));
    await context.SaveChangesAsync();

    var random = new Random(11);
    var transactions = new List<Transaction>();
    for (var month = 0; month < 6; month++)
    {
      var anchor = Now.AddMonths(-month);
      transactions.Add(Income(checking.Id, 4200m, "PAYROLL DEPOSIT", "Meridian Health", anchor.AddDays(-1)));
      transactions.Add(Income(checking.Id, 4200m, "PAYROLL DEPOSIT", "Meridian Health", anchor.AddDays(-15)));
      transactions.Add(Spend(checking.Id, 1900m, "RENT PAYMENT", "Bayview Apartments", TransactionCategory.Rent, anchor.AddDays(-2)));
      for (var i = 0; i < 4; i++)
        transactions.Add(Spend(checking.Id, Math.Round(45m + (decimal)random.NextDouble() * 120m, 2),
            "WHOLE FOODS MKT", "Whole Foods", TransactionCategory.Groceries, anchor.AddDays(-3 - i * 7)));
      for (var i = 0; i < 5; i++)
        transactions.Add(Spend(checking.Id, Math.Round(18m + (decimal)random.NextDouble() * 70m, 2),
            "DINING OUT", i % 2 == 0 ? "Thai Basil" : "El Toro Loco", TransactionCategory.DiningOut, anchor.AddDays(-2 - i * 5)));
      for (var i = 0; i < 2; i++)
        transactions.Add(Spend(checking.Id, Math.Round(32m + (decimal)random.NextDouble() * 28m, 2),
            "SHELL OIL", "Shell", TransactionCategory.Transportation, anchor.AddDays(-6 - i * 12)));
      transactions.Add(Spend(checking.Id, 15.49m, "NETFLIX.COM", "Netflix", TransactionCategory.Subscriptions, anchor.AddDays(-8)));
      transactions.Add(Spend(checking.Id, 11.99m, "SPOTIFY USA", "Spotify", TransactionCategory.Subscriptions, anchor.AddDays(-12)));
      transactions.Add(Spend(checking.Id, Math.Round(120m + (decimal)random.NextDouble() * 90m, 2),
          "CITY POWER & LIGHT", "City Power", TransactionCategory.Utilities, anchor.AddDays(-9)));
      for (var i = 0; i < 2; i++)
        transactions.Add(Spend(checking.Id, Math.Round(25m + (decimal)random.NextDouble() * 110m, 2),
            "AMAZON MKTPL", "Amazon", TransactionCategory.Shopping, anchor.AddDays(-5 - i * 11)));
    }
    context.Transactions.AddRange(transactions);
    await context.SaveChangesAsync();

    context.SavingsGoals.Add(new SavingsGoal
    {
      UserId = user.Id,
      Type = SavingsGoalType.Personal,
      Name = "Japan Trip",
      Description = "Two weeks in spring",
      TargetAmount = 8000m,
      CurrentAmount = 5200m,
      AllocationPercentage = 12m,
      TargetDate = Now.AddMonths(7),
      IconName = "airplane",
      ColorTheme = "info",
      IsActive = true,
      IsCompleted = false,
      CreatedAt = Now.AddMonths(-5),
      UpdatedAt = Now
    });

    UserAiMemory Memory(string type, string key, string value, string display) => new()
    {
      UserId = user.Id,
      MemoryType = type,
      MemoryKey = key,
      MemoryValue = value,
      DisplayText = display,
      Confidence = 1.0m,
      LearnedAt = Now.AddMonths(-2),
      UsageCount = 3,
      IsActive = true,
      CreatedAt = Now.AddMonths(-2),
      UpdatedAt = Now
    };
    context.Set<UserAiMemory>().AddRange(
      Memory(AiMemoryTypes.Goal, "japan_trip", "saving $8,000 for a spring trip to Japan",
          "Saving $8,000 for a two-week Japan trip in spring"),
      Memory(AiMemoryTypes.Family, "kids", "has two kids",
          "Has two kids"),
      Memory(AiMemoryTypes.FinancialHabit, "weekend_dining", "tends to overspend on dining out on weekends",
          "Tends to overspend on dining out on weekends"),
      Memory(AiMemoryTypes.Preference, "answer_style", "prefers direct answers with concrete numbers",
          "Prefers direct answers with concrete numbers"));
    await context.SaveChangesAsync();
    Console.WriteLine("  Seeded Sage Askins (demo-ai-chat) — 6 months of data + 4 AI memories");
  }

  // ── Wren Summers — Money Wrapped month ────────────────────────────────────
  // One immaculate CLOSED month: obvious top category (dining), deliberate
  // zero-spend days, in-month savings transfer. Nothing else touches this data.
  private static async Task SeedWrenWrappedAsync(BuoyAIDbContext context)
  {
    var user = CreateDemoUser("demo-wrapped", "demo-wrapped@buoy-ai.com", "Wren", "Summers");
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var checking = CreateChecking(user.Id, "wrapped", 5900m);
    context.Accounts.Add(checking);
    await context.SaveChangesAsync();

    var budget = CreateMonthlyBudget(user.Id, "Wren's Monthly Budget");
    context.Budgets.Add(budget);
    await context.SaveChangesAsync();
    context.BudgetCategories.AddRange(
      Category(budget.Id, TransactionCategory.Salary, true, 7600m, 0),
      Category(budget.Id, TransactionCategory.Groceries, false, 520m, 1),
      Category(budget.Id, TransactionCategory.DiningOut, false, 650m, 2),
      Category(budget.Id, TransactionCategory.Transportation, false, 220m, 3),
      Category(budget.Id, TransactionCategory.Entertainment, false, 180m, 4),
      Category(budget.Id, TransactionCategory.Subscriptions, false, 80m, 5),
      Category(budget.Id, TransactionCategory.Utilities, false, 240m, 6),
      Category(budget.Id, TransactionCategory.Shopping, false, 300m, 7));
    await context.SaveChangesAsync();

    var prevMonthStart = new DateTime(Now.Year, Now.Month, 1, 12, 0, 0, DateTimeKind.Utc).AddMonths(-1);
    DateTime Day(int d) => prevMonthStart.AddDays(d - 1);

    var transactions = new List<Transaction>
    {
      Income(checking.Id, 3800m, "PAYROLL DEPOSIT", "Coastal Studio", Day(1)),
      Income(checking.Id, 3800m, "PAYROLL DEPOSIT", "Coastal Studio", Day(15)),
      // Savings sweep mid-month.
      Spend(checking.Id, 600m, "TRANSFER TO SAVINGS", "Internal Transfer", TransactionCategory.Transfer, Day(16))
    };

    // Dining is the unmistakable top category (~$620 across 14 visits).
    var diningDays = new[] { 2, 3, 5, 6, 9, 10, 12, 14, 17, 19, 21, 24, 26, 28 };
    var diningAmounts = new[] { 34.50m, 52.20m, 18.75m, 61.40m, 27.90m, 45.00m, 38.25m, 72.80m, 24.60m, 55.10m, 31.45m, 68.90m, 42.30m, 46.85m };
    for (var i = 0; i < diningDays.Length; i++)
      transactions.Add(Spend(checking.Id, diningAmounts[i], "DINING OUT",
          i % 3 == 0 ? "Saltline Bistro" : i % 3 == 1 ? "Nori Sushi" : "The Copper Pot",
          TransactionCategory.DiningOut, Day(diningDays[i])));

    var groceryDays = new[] { 2, 6, 10, 13, 17, 20, 24, 27 };
    var groceryAmounts = new[] { 64.20m, 48.75m, 71.10m, 39.95m, 55.40m, 44.85m, 68.30m, 51.60m };
    for (var i = 0; i < groceryDays.Length; i++)
      transactions.Add(Spend(checking.Id, groceryAmounts[i], "TRADER JOES", "Trader Joe's",
          TransactionCategory.Groceries, Day(groceryDays[i])));

    var gasDays = new[] { 5, 12, 19, 26 };
    foreach (var d in gasDays)
      transactions.Add(Spend(checking.Id, 41.20m, "SHELL OIL", "Shell",
          TransactionCategory.Transportation, Day(d)));

    transactions.Add(Spend(checking.Id, 32.00m, "AMC THEATRES", "AMC", TransactionCategory.Entertainment, Day(9)));
    transactions.Add(Spend(checking.Id, 58.50m, "TICKETMASTER", "Ticketmaster", TransactionCategory.Entertainment, Day(21)));
    transactions.Add(Spend(checking.Id, 15.49m, "NETFLIX.COM", "Netflix", TransactionCategory.Subscriptions, Day(8)));
    transactions.Add(Spend(checking.Id, 11.99m, "SPOTIFY USA", "Spotify", TransactionCategory.Subscriptions, Day(12)));
    transactions.Add(Spend(checking.Id, 142.30m, "CITY POWER & LIGHT", "City Power", TransactionCategory.Utilities, Day(7)));
    transactions.Add(Spend(checking.Id, 89.90m, "WATER & SEWER", "City Water", TransactionCategory.Utilities, Day(14)));
    transactions.Add(Spend(checking.Id, 76.45m, "TARGET T-1042", "Target", TransactionCategory.Shopping, Day(10)));
    transactions.Add(Spend(checking.Id, 54.20m, "AMAZON MKTPL", "Amazon", TransactionCategory.Shopping, Day(23)));

    // Light current-month activity so the app doesn't look abandoned today.
    transactions.Add(Income(checking.Id, 3800m, "PAYROLL DEPOSIT", "Coastal Studio", Now.AddDays(-1)));
    transactions.Add(Spend(checking.Id, 47.80m, "TRADER JOES", "Trader Joe's", TransactionCategory.Groceries, Now.AddDays(-1)));

    context.Transactions.AddRange(transactions);
    await context.SaveChangesAsync();

    context.SavingsGoals.Add(new SavingsGoal
    {
      UserId = user.Id,
      Type = SavingsGoalType.Personal,
      Name = "House Fund",
      Description = "Down payment",
      TargetAmount = 30000m,
      CurrentAmount = 9600m,
      AllocationPercentage = 15m,
      TargetDate = Now.AddYears(2),
      IconName = "home",
      ColorTheme = "primary",
      IsActive = true,
      IsCompleted = false,
      CreatedAt = Now.AddMonths(-8),
      UpdatedAt = Now
    });
    await context.SaveChangesAsync();
    Console.WriteLine("  Seeded Wren Summers (demo-wrapped) — one immaculate closed month");
  }

  // ── Nova Spendlow — no-spend challenge + streaks ──────────────────────────
  // Teens streak with a banked freeze, and a Categorization Champ challenge
  // seeded one step from completion so it completes on camera.
  private static async Task SeedNovaNoSpendAsync(BuoyAIDbContext context)
  {
    var user = CreateDemoUser("demo-nospend", "demo-nospend@buoy-ai.com", "Nova", "Spendlow");
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var checking = CreateChecking(user.Id, "nospend", 3300m);
    context.Accounts.Add(checking);
    await context.SaveChangesAsync();

    var budget = CreateMonthlyBudget(user.Id, "Nova's Monthly Budget");
    context.Budgets.Add(budget);
    await context.SaveChangesAsync();
    context.BudgetCategories.AddRange(
      Category(budget.Id, TransactionCategory.Salary, true, 4200m, 0),
      Category(budget.Id, TransactionCategory.Groceries, false, 480m, 1),
      Category(budget.Id, TransactionCategory.DiningOut, false, 200m, 2),
      Category(budget.Id, TransactionCategory.Transportation, false, 200m, 3));
    await context.SaveChangesAsync();

    var transactions = new List<Transaction>
    {
      Income(checking.Id, 2100m, "PAYROLL DEPOSIT", "Fernwood Books", Now.AddDays(-2)),
      Income(checking.Id, 2100m, "PAYROLL DEPOSIT", "Fernwood Books", Now.AddDays(-16)),
      Spend(checking.Id, 58.90m, "KROGER #212", "Kroger", TransactionCategory.Groceries, Now.AddDays(-4)),
      Spend(checking.Id, 36.75m, "SHELL OIL", "Shell", TransactionCategory.Transportation, Now.AddDays(-7))
    };
    // A handful of uncategorized transactions: categorizing ONE on camera
    // completes the challenge below.
    for (var i = 0; i < 6; i++)
      transactions.Add(CreateTransaction(checking.Id, 12.40m + i * 3.15m, TransactionType.Debit,
          "SQ *CORNER CAFE", "Corner Cafe", (int)TransactionCategory.Uncategorized, Now.AddDays(-1 - i * 2)));
    context.Transactions.AddRange(transactions);
    await context.SaveChangesAsync();

    context.Set<UserStreak>().AddRange(
      new UserStreak
      {
        UserId = user.Id,
        StreakType = "under_budget",
        CurrentCount = 16,
        BestCount = 21,
        LastIncrementDate = Now.Date.AddDays(-1),
        StreakStartDate = Now.Date.AddDays(-16),
        StreakFreezes = 2,
        CreatedAt = Now.AddMonths(-2),
        UpdatedAt = Now
      },
      new UserStreak
      {
        UserId = user.Id,
        StreakType = "login",
        CurrentCount = 9,
        BestCount = 30,
        LastIncrementDate = Now.Date.AddDays(-1),
        StreakStartDate = Now.Date.AddDays(-9),
        StreakFreezes = 1,
        CreatedAt = Now.AddMonths(-2),
        UpdatedAt = Now
      });

    context.Set<UserChallenge>().Add(new UserChallenge
    {
      UserId = user.Id,
      ChallengeType = "categorize_transactions",
      Title = "Categorization Champ",
      Description = "Categorize 5 transactions",
      TargetValue = 5m,
      CurrentValue = 4m,
      RewardPoints = 25,
      StartDate = Now.Date,
      EndDate = Now.Date.AddDays(1).AddSeconds(-1),
      Status = "active",
      CreatedAt = Now.Date,
    });
    await context.SaveChangesAsync();
    Console.WriteLine("  Seeded Nova Spendlow (demo-nospend) — 16d streak, challenge 1 step from done");
  }

  // ── The Marlowes — family sharing, two views of one family ───────────────
  // Dana owns the family (full control); Theo is a read-only member. Both see the same family
  // budget and the shared checking account, so a film can show the owner's editable view next
  // to the read-only one. The only other families on a dev slot are test fixtures with
  // placeholder names. Spending is this month's, light enough to stay green on camera.
  private static async Task SeedMarloweFamilyAsync(BuoyAIDbContext context)
  {
    var dana = CreateDemoUser("demo-family-owner", "demo-family-owner@buoy-ai.com", "Dana", "Marlowe");
    var theo = CreateDemoUser("demo-family-viewer", "demo-family-viewer@buoy-ai.com", "Theo", "Marlowe");
    context.Users.AddRange(dana, theo);
    await context.SaveChangesAsync();

    var family = new Family
    {
      Name = "The Marlowes",
      CreatedByUserId = dana.Id,
      CreatedAt = Now.AddMonths(-5),
      UpdatedAt = Now
    };
    context.Families.Add(family);
    await context.SaveChangesAsync(); // separate save: family and creator reference each other

    dana.FamilyId = family.Id;
    theo.FamilyId = family.Id;
    context.FamilyMembers.AddRange(
      new FamilyMember { FamilyId = family.Id, UserId = dana.Id, Role = FamilyMemberRole.Owner, JoinedAt = Now.AddMonths(-5), IsActive = true },
      new FamilyMember { FamilyId = family.Id, UserId = theo.Id, Role = FamilyMemberRole.ReadOnly, JoinedAt = Now.AddMonths(-4), IsActive = true });
    await context.SaveChangesAsync();

    var checking = CreateChecking(dana.Id, "marlowe", 5820m); // not "family": the test family owns manual-family-checking
    checking.FamilyId = family.Id;
    checking.Name = "Family Checking";
    checking.OfficialName = "Joint Checking";
    checking.Mask = "2207";
    context.Accounts.Add(checking);
    await context.SaveChangesAsync();

    var budget = CreateMonthlyBudget(dana.Id, "Marlowe Family Budget");
    budget.FamilyId = family.Id;
    budget.Type = BudgetType.Family;
    context.Budgets.Add(budget);
    await context.SaveChangesAsync();

    context.BudgetCategories.AddRange(
      Category(budget.Id, TransactionCategory.Salary, true, 8400m, 0),
      Category(budget.Id, TransactionCategory.Rent, false, 2100m, 1),
      Category(budget.Id, TransactionCategory.Groceries, false, 900m, 2),
      Category(budget.Id, TransactionCategory.Utilities, false, 260m, 3),
      Category(budget.Id, TransactionCategory.DiningOut, false, 320m, 4),
      Category(budget.Id, TransactionCategory.Education, false, 180m, 5),
      Category(budget.Id, TransactionCategory.Entertainment, false, 140m, 6));
    await context.SaveChangesAsync();

    context.Transactions.AddRange(
      Income(checking.Id, 4200m, "PAYROLL DEPOSIT", "Northwind Health", Now.AddDays(-1)),
      Spend(checking.Id, 2100m, "RENT PAYMENT", "Maple Court Apartments", TransactionCategory.Rent, Now.AddDays(-2)),
      Spend(checking.Id, 126.43m, "COSTCO WHOLESALE", "Costco", TransactionCategory.Groceries, Now.AddDays(-1)),
      Spend(checking.Id, 58.17m, "TRADER JOES", "Trader Joe's", TransactionCategory.Groceries, Now.AddDays(-3)),
      Spend(checking.Id, 94.60m, "CITY POWER AND LIGHT", "City Power & Light", TransactionCategory.Utilities, Now.AddDays(-2)),
      Spend(checking.Id, 42.80m, "OLIVE GARDEN", "Olive Garden", TransactionCategory.DiningOut, Now.AddDays(-1)),
      Spend(checking.Id, 65.00m, "RIVERSIDE SWIM CLUB", "Riverside Swim Club", TransactionCategory.Education, Now.AddDays(-3)),
      Spend(checking.Id, 15.49m, "NETFLIX.COM", "Netflix", TransactionCategory.Entertainment, Now.AddDays(-2)));
    await context.SaveChangesAsync();
    Console.WriteLine("  Seeded The Marlowes (demo-family-owner owner, demo-family-viewer read-only)");
  }
}
