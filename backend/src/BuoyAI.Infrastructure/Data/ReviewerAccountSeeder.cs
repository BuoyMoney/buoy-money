using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.DTOs.Gamification;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.Infrastructure.Data;

/// <summary>
/// Seeds the App Store / Play Store reviewer account (and its family) so it exists in
/// Staging and Development automatically. This account was originally hand-created on prod;
/// this seeder makes it version-controlled and reproducible after a fresh Staging database.
///
/// The dataset mirrors the PROD reviewer's structure so staging/dev look identical to what a
/// store reviewer would see on prod:
///   - 3 accounts (Checking, Savings, Visa)
///   - 3 budgets: a PERSONAL primary budget with count-up categories, a FAMILY budget, and a
///     child ALLOWANCE budget (previous-month period)
///   - ~5 months of recurring transactions so "previous months" of history exist
///   - 3 personal savings goals + a prior-period snapshot on the two main budgets
///
/// Reviewer credentials (share with the app-store reviewer), one family per store:
/// - Google Play: play-reviewer@buoy-ai.com (owner, Premium) + alex-child@buoy-ai.com (member)
/// - Apple:       apple-reviewer@buoy-ai.com (owner, Premium) + jamie-child@buoy-ai.com (member)
/// All four share one password that is NOT in source: it comes from the Reviewer__Password setting
/// (Development falls back to a local-only default). Existing accounts keep whatever password they have.
///
/// Why two: per-account state such as AI data-sharing consent (App Store 5.1.2(i)) follows the
/// account, not the device. When both stores shared play-reviewer, a grant made by the Play
/// reviewer on Android meant Apple's reviewer never saw the consent sheet on iPad. Each store
/// gets its own account so each reviewer sees a first-run app.
///
/// This is PURE DATA SEEDING — it runs AFTER migrations, exactly like DataSeeder.SeedAdminUsers.
/// It is idempotent: if the reviewer user already exists, it returns without doing anything.
/// </summary>
public static class ReviewerAccountSeeder
{
  /// <summary>One store's reviewer family. Same dataset for each.</summary>
  /// <remarks>
  /// <c>AccountIdPrefix</c> keeps the seeded accounts' PlaidAccountId unique per family
  /// (IX_Accounts_PlaidAccountId is a unique index). Play keeps the original values so the
  /// existing prod rows still match.
  /// </remarks>
  internal sealed record ReviewerProfile(
    string Email, string Username, string ChildEmail, string ChildUsername, string AccountIdPrefix);

  internal static readonly ReviewerProfile Play =
    new("play-reviewer@buoy-ai.com", "play-reviewer", "alex-child@buoy-ai.com", "alex-child", "manual-reviewer");

  internal static readonly ReviewerProfile Apple =
    new("apple-reviewer@buoy-ai.com", "apple-reviewer", "jamie-child@buoy-ai.com", "jamie-child", "manual-apple-reviewer");

  internal static readonly ReviewerProfile[] Profiles = [Play, Apple];

  /// <summary>Every seeded reviewer login, for callers that must never wipe them.</summary>
  public static readonly string[] AllEmails =
    Profiles.SelectMany(p => new[] { p.Email, p.ChildEmail }).ToArray();
  /// <summary>Local-dev only. Never used outside Development; hosted environments must set Reviewer__Password.</summary>
  internal const string DevOnlyPassword = "BuoyReviewDev-local1";
  private static readonly DateTime Now = DateTime.UtcNow;

  // System category ids (1-99) -> display names, used for denormalized snapshot rows.
  private static readonly Dictionary<int, string> CategoryNames = new()
  {
    [(int)TransactionCategory.Salary] = "Salary",
    [(int)TransactionCategory.OtherIncome] = "Other Income",
    [(int)TransactionCategory.Rent] = "Rent/Mortgage",
    [(int)TransactionCategory.Utilities] = "Utilities",
    [(int)TransactionCategory.Subscriptions] = "Subscriptions",
    [(int)TransactionCategory.Groceries] = "Groceries",
    [(int)TransactionCategory.DiningOut] = "Dining Out",
    [(int)TransactionCategory.Transportation] = "Transportation",
    [(int)TransactionCategory.Entertainment] = "Entertainment",
    [(int)TransactionCategory.Shopping] = "Shopping",
    [(int)TransactionCategory.Travel] = "Travel",
    [(int)TransactionCategory.PersonalCare] = "Personal Care",
    [(int)TransactionCategory.Education] = "Education",
    [(int)TransactionCategory.Uncategorized] = "Uncategorized",
  };

  public static async Task SeedReviewerAccountAsync(BuoyAIDbContext context, string? password)
  {
    foreach (var profile in Profiles)
    {
      await SeedReviewerAccountAsync(context, profile, password);
    }
  }

  private static async Task SeedReviewerAccountAsync(BuoyAIDbContext context, ReviewerProfile profile, string? password)
  {
    // Idempotent guard: safe to run on every app startup. An EXISTING reviewer
    // (including the hand-created prod one) still gets the enrichment pass so
    // new-feature data (streak types, badges, fresh months) stays evergreen.
    var existingReviewer = await context.Users.FirstOrDefaultAsync(u => u.Email == profile.Email);
    if (existingReviewer != null)
    {
      Console.WriteLine($"Reviewer account {profile.Email} already exists, running enrichment only");
      await EnrichReviewerAsync(context, existingReviewer.Id);
      return;
    }

    if (string.IsNullOrWhiteSpace(password))
    {
      Console.WriteLine($"Reviewer account {profile.Email} not created: Reviewer__Password is not set");
      return;
    }

    Console.WriteLine("=== Seeding App Store / Play Store reviewer account ===");

    // Wrap the ENTIRE seed in one transaction so a mid-way failure (e.g. an FK violation on a
    // system category that hasn't been seeded yet) rolls the whole thing back instead of leaving a
    // partial reviewer (user + accounts, no transactions/budget/goals) that the idempotency guard
    // above would then skip forever. It also makes the two-backend-container boot race safe: one
    // commits, the other blocks on the unique Email index and rolls back cleanly. The context uses
    // NpgsqlRetryingExecutionStrategy, so the manual transaction must run inside an execution
    // strategy (same pattern as AuthService).
    var strategy = context.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
      await using var transaction = await context.Database.BeginTransactionAsync();
      await SeedReviewerDataAsync(context, profile, password);
      await transaction.CommitAsync();
    });

    var reviewer = await context.Users.FirstOrDefaultAsync(u => u.Email == profile.Email);
    if (reviewer != null)
    {
      await EnrichReviewerAsync(context, reviewer.Id);
    }
  }

  /// <summary>
  /// Idempotent enrichment for an existing reviewer: creates any MISSING streak
  /// rows (never touches existing ones), any missing core badges, and tops up
  /// the current + previous month with the recurring transaction set when a
  /// month has gone completely stale. Runs on every startup in every
  /// environment, so the store reviewer always lands on a live-looking app:
  /// ticking count-ups, streak medallions, a populated Wrapped story.
  /// </summary>
  internal static async Task EnrichReviewerAsync(BuoyAIDbContext context, int reviewerId)
  {
    // 1) Streak rows for every type the Insights page renders. Values chosen to
    //    look lived-in (heat tiers, banked freezes) without being implausible.
    var wantedStreaks = new (string Type, int Current, int Best, int Freezes)[]
    {
      ("under_budget", 9, 21, 2),
      ("family_under_budget", 6, 12, 1),
      ("login", 5, 14, 1),
      ("categorization", 3, 8, 0),
      ("savings_goal", 2, 6, 0),
    };
    var existingTypes = await context.Set<UserStreak>()
      .Where(st => st.UserId == reviewerId)
      .Select(st => st.StreakType)
      .ToListAsync();
    var yesterday = DateTime.UtcNow.Date.AddDays(-1);
    foreach (var (type, current, best, freezes) in wantedStreaks)
    {
      if (existingTypes.Contains(type)) continue;
      context.Set<UserStreak>().Add(new UserStreak
      {
        UserId = reviewerId,
        StreakType = type,
        CurrentCount = current,
        BestCount = best,
        StreakFreezes = freezes,
        LastIncrementDate = yesterday,
        StreakStartDate = yesterday.AddDays(-current + 1),
        CreatedAt = DateTime.UtcNow.AddMonths(-3),
        UpdatedAt = DateTime.UtcNow
      });
    }

    // 2) Core badges (insert-if-missing, IsNew=false so the reviewer is not
    //    greeted by a wall of celebrations; data-derived badges still fire live
    //    on the first achievements visit, which demos the celebration).
    var wantedBadges = new[]
    {
      AchievementDefinitions.FirstBudget,
      AchievementDefinitions.FirstSavingsGoal,
      AchievementDefinitions.QuizComplete,
      AchievementDefinitions.WeekStreak,
      AchievementDefinitions.AllAccountsLinked,
    };
    var existingKeys = await context.Set<UserAchievement>()
      .Where(a => a.UserId == reviewerId)
      .Select(a => a.AchievementKey)
      .ToListAsync();
    var earnedAt = DateTime.UtcNow.AddMonths(-4);
    foreach (var key in wantedBadges)
    {
      if (existingKeys.Contains(key) || !AchievementDefinitions.All.TryGetValue(key, out var def)) continue;
      context.Set<UserAchievement>().Add(new UserAchievement
      {
        UserId = reviewerId,
        AchievementKey = key,
        Title = def.Title,
        Description = def.Description,
        Icon = def.Icon,
        Category = def.Category,
        Points = def.Points,
        EarnedAt = earnedAt,
        IsNew = false
      });
      earnedAt = earnedAt.AddDays(11);
    }
    await context.SaveChangesAsync();

    // 3) Freshness top-up: if the current or previous month has NO transactions
    //    at all, replay the recurring monthly set into it so Wrapped, cash flow,
    //    and count-up spend never go dead between deploys.
    var accounts = await context.Accounts
      .Where(a => a.UserId == reviewerId && a.IsActive)
      .ToListAsync();
    var checking = accounts.FirstOrDefault(a => a.Type == AccountType.Checking);
    var savings = accounts.FirstOrDefault(a => a.Type == AccountType.Savings);
    var credit = accounts.FirstOrDefault(a => a.Type == AccountType.CreditCard);
    if (checking == null)
    {
      return; // hand-created structure we do not recognize; leave it alone
    }

    var added = 0;
    var accountIds = accounts.Select(a => a.Id).ToList();
    for (var m = 1; m >= 0; m--)
    {
      var anchor = DateTime.UtcNow.AddMonths(-m);
      var monthStart = new DateTime(anchor.Year, anchor.Month, 1, 0, 0, 0, DateTimeKind.Utc);
      var monthEnd = monthStart.AddMonths(1);
      var hasAny = await context.Transactions.AnyAsync(t =>
        accountIds.Contains(t.AccountId) && t.Date >= monthStart && t.Date < monthEnd);
      if (hasAny) continue;
      var txns = MonthlyTransactionSet(checking, savings ?? checking, credit ?? checking,
        anchor.Year, anchor.Month);
      context.Transactions.AddRange(txns);
      added += txns.Count;
    }
    if (added > 0) await context.SaveChangesAsync();
    Console.WriteLine($"Reviewer enrichment complete ({added} topped-up transactions)");
  }

  private static async Task SeedReviewerDataAsync(BuoyAIDbContext context, ReviewerProfile profile, string password)
  {
    var passwordHash = AuthService.HashPassword(password);

    // 1. Reviewer (family owner) — Premium.
    var reviewer = new User
    {
      Username = profile.Username,
      PasswordHash = passwordHash,
      Email = profile.Email,
      FirstName = "Riley",
      LastName = "Reviewer",
      Currency = "USD",
      CreatedAt = Now.AddMonths(-6),
      UpdatedAt = Now,
      SubscriptionTier = SubscriptionTier.Premium,
      SubscriptionStartDate = Now.AddMonths(-6),
      SubscriptionEndDate = Now.AddMonths(6),
      IsEmailVerified = true,
      EmailVerifiedAt = Now.AddMonths(-6),
      HasUsedTrial = true,
      IsTrialActive = false
    };
    context.Users.Add(reviewer);

    // 2. Child (family member) — PremiumInherited from the family.
    var child = new User
    {
      Username = profile.ChildUsername,
      PasswordHash = passwordHash,
      Email = profile.ChildEmail,
      FirstName = "Alex",
      LastName = "Reviewer",
      Currency = "USD",
      CreatedAt = Now.AddMonths(-5),
      UpdatedAt = Now,
      SubscriptionTier = SubscriptionTier.PremiumInherited,
      IsEmailVerified = true,
      EmailVerifiedAt = Now.AddMonths(-5)
    };
    context.Users.Add(child);

    // Save so the users get IDs before we reference them from the family.
    await context.SaveChangesAsync();

    // 3. Family owned by the reviewer.
    var family = new Family
    {
      Name = "Reviewer Family",
      CreatedByUserId = reviewer.Id,
      CreatedAt = Now.AddMonths(-6),
      UpdatedAt = Now
    };
    context.Families.Add(family);
    await context.SaveChangesAsync();

    // Attach both users to the family.
    reviewer.FamilyId = family.Id;
    child.FamilyId = family.Id;

    var members = new List<FamilyMember>
    {
      new FamilyMember
      {
        FamilyId = family.Id,
        UserId = reviewer.Id,
        Role = FamilyMemberRole.Owner,
        JoinedAt = Now.AddMonths(-6),
        IsActive = true
      },
      new FamilyMember
      {
        FamilyId = family.Id,
        UserId = child.Id,
        Role = FamilyMemberRole.ReadOnly,
        JoinedAt = Now.AddMonths(-5),
        IsActive = true
      }
    };
    context.FamilyMembers.AddRange(members);
    await context.SaveChangesAsync();

    // 4. Accounts for the reviewer (Checking, Savings, Credit Card) — mirrors prod.
    var checking = new Account
    {
      UserId = reviewer.Id,
      PlaidAccountId = $"{profile.AccountIdPrefix}-checking",
      Name = "BuoyAI Checking",
      OfficialName = "BuoyAI Total Checking",
      Nickname = "Main Account",
      Mask = "4001",
      Type = AccountType.Checking,
      Subtype = "checking",
      CurrentBalance = 4250.75m,
      AvailableBalance = 4250.75m,
      Currency = "USD",
      IsActive = true,
      CreatedAt = Now.AddMonths(-6),
      UpdatedAt = Now
    };
    context.Accounts.Add(checking);

    var savings = new Account
    {
      UserId = reviewer.Id,
      PlaidAccountId = $"{profile.AccountIdPrefix}-savings",
      Name = "BuoyAI Savings",
      OfficialName = "BuoyAI High-Yield Savings",
      Mask = "9823",
      Type = AccountType.Savings,
      Subtype = "savings",
      CurrentBalance = 12800.00m,
      AvailableBalance = 12800.00m,
      Currency = "USD",
      IsActive = true,
      CreatedAt = Now.AddMonths(-6),
      UpdatedAt = Now
    };
    context.Accounts.Add(savings);

    var credit = new Account
    {
      UserId = reviewer.Id,
      PlaidAccountId = $"{profile.AccountIdPrefix}-credit",
      Name = "BuoyAI Visa",
      OfficialName = "BuoyAI Rewards Visa",
      Mask = "7731",
      Type = AccountType.CreditCard,
      Subtype = "credit card",
      CurrentBalance = -1340.22m,
      AvailableBalance = 8659.78m,
      Currency = "USD",
      IsActive = true,
      CreatedAt = Now.AddMonths(-6),
      UpdatedAt = Now
    };
    context.Accounts.Add(credit);

    await context.SaveChangesAsync();

    // 5. ~5 months of recurring transactions (current month + 4 prior) so the app has
    //    "previous months" of history for trends, month pickers and budget periods.
    var transactions = new List<Transaction>();

    for (int m = 4; m >= 0; m--)
    {
      var anchor = Now.AddMonths(-m);
      transactions.AddRange(MonthlyTransactionSet(checking, savings, credit, anchor.Year, anchor.Month));
    }

    context.Transactions.AddRange(transactions);
    await context.SaveChangesAsync();

    // Period windows.
    var thisStart = new DateTime(Now.Year, Now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    var thisEnd = new DateTime(Now.Year, Now.Month, DateTime.DaysInMonth(Now.Year, Now.Month), 23, 59, 59, DateTimeKind.Utc);
    var prevAnchor = Now.AddMonths(-1);
    var prevStart = new DateTime(prevAnchor.Year, prevAnchor.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    var prevEnd = new DateTime(prevAnchor.Year, prevAnchor.Month, DateTime.DaysInMonth(prevAnchor.Year, prevAnchor.Month), 23, 59, 59, DateTimeKind.Utc);

    // 6a. PERSONAL primary budget with count-up categories (the reviewer's personal budget).
    var personalBudget = new Budget
    {
      UserId = reviewer.Id,
      FamilyId = family.Id,
      Name = "Monthly Budget",
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = thisStart,
      CurrentPeriodEnd = thisEnd,
      LastReset = thisStart,
      AutoReset = true,
      TotalIncome = 1500.00m,
      TotalExpenses = 1045.00m,
      IsActive = true,
      Type = BudgetType.Personal,
      IsPrimary = true,
      RolloverMode = ChildBudgetRolloverMode.RolloverToSelf,
      CreatedAt = Now.AddMonths(-6),
      UpdatedAt = Now
    };
    context.Budgets.Add(personalBudget);
    await context.SaveChangesAsync();

    var personalCats = new List<BudgetCategory>
    {
      Cat(personalBudget.Id, TransactionCategory.Subscriptions, 120.00m, 0, countUp: true, rollover: 37.04m),
      Cat(personalBudget.Id, TransactionCategory.DiningOut, 200.00m, 1, countUp: true, rollover: 85.85m),
      Cat(personalBudget.Id, TransactionCategory.Transportation, 175.00m, 2, countUp: true, rollover: 50.05m),
      Cat(personalBudget.Id, TransactionCategory.Entertainment, 75.00m, 3, countUp: true, rollover: 43.00m),
      Cat(personalBudget.Id, TransactionCategory.Shopping, 200.00m, 4, countUp: true, rollover: 68.79m),
      Cat(personalBudget.Id, TransactionCategory.Travel, 200.00m, 5, countUp: true, rollover: 80.00m),
      Cat(personalBudget.Id, TransactionCategory.PersonalCare, 75.00m, 6, countUp: true, rollover: 40.00m),
    };
    context.BudgetCategories.AddRange(personalCats);
    await context.SaveChangesAsync();

    // 6b. FAMILY budget (income + shared household expenses).
    var familyBudget = new Budget
    {
      UserId = reviewer.Id,
      FamilyId = family.Id,
      Name = "Family Budget",
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = thisStart,
      CurrentPeriodEnd = thisEnd,
      LastReset = thisStart,
      AutoReset = true,
      TotalIncome = 5500.00m,
      TotalExpenses = 2630.00m,
      IsActive = true,
      Type = BudgetType.Family,
      IsPrimary = false,
      RolloverMode = ChildBudgetRolloverMode.RolloverToSelf,
      CreatedAt = Now.AddMonths(-6),
      UpdatedAt = Now
    };
    context.Budgets.Add(familyBudget);
    await context.SaveChangesAsync();

    var familyCats = new List<BudgetCategory>
    {
      Cat(familyBudget.Id, TransactionCategory.Salary, 5500.00m, 0, isIncome: true, rollover: 5500.00m),
      Cat(familyBudget.Id, TransactionCategory.Rent, 1500.00m, 1, rollover: 50.00m),
      Cat(familyBudget.Id, TransactionCategory.Utilities, 200.00m, 2, rollover: 11.59m),
      Cat(familyBudget.Id, TransactionCategory.Groceries, 400.00m, 3, countUp: true, rollover: 73.49m),
      Cat(familyBudget.Id, TransactionCategory.Education, 150.00m, 4, rollover: 150.00m),
      Cat(familyBudget.Id, TransactionCategory.Uncategorized, 150.00m, 5, rollover: -248.11m),
    };
    context.BudgetCategories.AddRange(familyCats);
    await context.SaveChangesAsync();

    // 6c. Child ALLOWANCE budget funded by the family budget (previous-month period).
    var allowanceBudget = new Budget
    {
      UserId = child.Id,
      FamilyId = family.Id,
      Name = "Alex's Allowance",
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = prevStart,
      CurrentPeriodEnd = prevEnd,
      LastReset = prevStart,
      AutoReset = true,
      TotalIncome = 0.00m,
      TotalExpenses = 150.00m,
      IsActive = true,
      Type = BudgetType.Allowance,
      IsPrimary = false,
      ParentBudgetId = familyBudget.Id,
      AllocatedAmount = 150.00m,
      RolloverMode = ChildBudgetRolloverMode.RolloverToSelf,
      CreatedAt = Now.AddMonths(-2),
      UpdatedAt = Now
    };
    context.Budgets.Add(allowanceBudget);
    await context.SaveChangesAsync();

    context.BudgetCategories.Add(Cat(allowanceBudget.Id, TransactionCategory.Uncategorized, 150.00m, 0));
    await context.SaveChangesAsync();

    // 7. Prior-period snapshots on the personal + family budgets so "budget history" is populated.
    await SnapshotAsync(context, personalBudget, personalCats, prevStart, prevEnd);
    await SnapshotAsync(context, familyBudget, familyCats, prevStart, prevEnd);

    // 8. Personal savings goals (mirrors prod).
    var goals = new List<SavingsGoal>
    {
      new SavingsGoal
      {
        UserId = reviewer.Id,
        Type = SavingsGoalType.Personal,
        Name = "Emergency Fund",
        Description = "6 months of expenses",
        TargetAmount = 15000.00m,
        CurrentAmount = 8911.50m,
        AllocationPercentage = 10.0m,
        TargetDate = Now.AddMonths(12),
        IconName = "shield",
        ColorTheme = "danger",
        IsActive = true,
        IsCompleted = false,
        CreatedAt = Now.AddMonths(-6),
        UpdatedAt = Now
      },
      new SavingsGoal
      {
        UserId = reviewer.Id,
        Type = SavingsGoalType.Personal,
        Name = "Hawaii Vacation",
        Description = "Summer trip",
        TargetAmount = 6000.00m,
        CurrentAmount = 2733.00m,
        AllocationPercentage = 5.0m,
        TargetDate = Now.AddMonths(9),
        IconName = "airplane",
        ColorTheme = "info",
        IsActive = true,
        IsCompleted = false,
        CreatedAt = Now.AddMonths(-4),
        UpdatedAt = Now
      },
      new SavingsGoal
      {
        UserId = reviewer.Id,
        Type = SavingsGoalType.Personal,
        Name = "Home Down Payment",
        Description = "Down payment fund",
        TargetAmount = 40000.00m,
        CurrentAmount = 15499.00m,
        AllocationPercentage = 15.0m,
        TargetDate = Now.AddYears(3),
        IconName = "home",
        ColorTheme = "primary",
        IsActive = true,
        IsCompleted = false,
        CreatedAt = Now.AddMonths(-5),
        UpdatedAt = Now
      }
    };
    context.SavingsGoals.AddRange(goals);
    await context.SaveChangesAsync();

    // 9. BuoyAI billing subscription (Premium, active) for the reviewer.
    var subscription = new UserSubscription
    {
      UserId = reviewer.Id,
      Tier = SubscriptionTier.Premium,
      BillingCycle = SubscriptionBillingCycle.Monthly,
      Amount = 9.99m,
      StartDate = Now.AddMonths(-6),
      NextBillingDate = Now.AddDays(14),
      Status = UserSubscriptionStatus.Active,
      CreatedAt = Now.AddMonths(-6),
      UpdatedAt = Now
    };
    context.UserSubscriptions.Add(subscription);
    await context.SaveChangesAsync();

    Console.WriteLine($"Seeded reviewer account {profile.Email} (family owner) and {profile.ChildEmail} (member)");
    Console.WriteLine($"  {transactions.Count} transactions over 5 months, 3 budgets " +
                      $"(personal count-up + family + allowance), {goals.Count} savings goals");
  }

  /// <summary>
  /// One month of the reviewer's recurring transaction set (payroll, rent,
  /// groceries, subscriptions, card spend, interest). Shared by the full seed
  /// and the enrichment freshness top-up so a replayed month looks identical.
  /// </summary>
  private static List<Transaction> MonthlyTransactionSet(
    Account checking, Account savings, Account credit, int y, int mo)
  {
    DateTime D(int day) => new DateTime(y, mo, Math.Min(day, DateTime.DaysInMonth(y, mo)), 12, 0, 0, DateTimeKind.Utc);

    Transaction Txn(Account account, decimal amount, TransactionType type, string description,
      string merchant, TransactionCategory category, DateTime date)
    {
      return new Transaction
      {
        AccountId = account.Id,
        PlaidTransactionId = $"manual-reviewer-{Guid.NewGuid():N}",
        Amount = Math.Abs(amount),
        Type = type,
        Description = description,
        MerchantName = merchant,
        CategoryId = (int)category,
        Date = date,
        IsPending = false,
        IsUserCategorized = false,
        CategorySource = "Seeded",
        CreatedAt = date,
        UpdatedAt = date
      };
    }

    return new List<Transaction>
    {
      Txn(checking, 2750.00m, TransactionType.Credit, "EMPLOYER PAYROLL", "Globex Corp", TransactionCategory.Salary, D(1)),
      Txn(checking, 2750.00m, TransactionType.Credit, "EMPLOYER PAYROLL", "Globex Corp", TransactionCategory.Salary, D(15)),
      Txn(checking, 1500.00m, TransactionType.Debit, "RENT PAYMENT", "Bay Property Mgmt", TransactionCategory.Rent, D(3)),
      Txn(checking, 92.40m, TransactionType.Debit, "WHOLE FOODS MKT", "Whole Foods", TransactionCategory.Groceries, D(5)),
      Txn(checking, 78.15m, TransactionType.Debit, "TRADER JOES", "Trader Joe's", TransactionCategory.Groceries, D(12)),
      Txn(checking, 104.60m, TransactionType.Debit, "COSTCO WHOLESALE", "Costco", TransactionCategory.Groceries, D(19)),
      Txn(checking, 66.30m, TransactionType.Debit, "SAFEWAY", "Safeway", TransactionCategory.Groceries, D(26)),
      Txn(checking, 132.00m, TransactionType.Debit, "CITY ELECTRIC", "City Electric", TransactionCategory.Utilities, D(8)),
      Txn(checking, 69.99m, TransactionType.Debit, "COMCAST INTERNET", "Comcast", TransactionCategory.Utilities, D(9)),
      Txn(checking, 15.99m, TransactionType.Debit, "NETFLIX", "Netflix", TransactionCategory.Subscriptions, D(7)),
      Txn(checking, 11.99m, TransactionType.Debit, "SPOTIFY", "Spotify", TransactionCategory.Subscriptions, D(14)),
      Txn(credit, 42.30m, TransactionType.Debit, "CHIPOTLE", "Chipotle", TransactionCategory.DiningOut, D(4)),
      Txn(credit, 28.75m, TransactionType.Debit, "STARBUCKS", "Starbucks", TransactionCategory.DiningOut, D(11)),
      Txn(credit, 63.40m, TransactionType.Debit, "OLIVE GARDEN", "Olive Garden", TransactionCategory.DiningOut, D(22)),
      Txn(credit, 51.40m, TransactionType.Debit, "SHELL OIL", "Shell", TransactionCategory.Transportation, D(6)),
      Txn(credit, 24.50m, TransactionType.Debit, "UBER TRIP", "Uber", TransactionCategory.Transportation, D(18)),
      Txn(credit, 89.99m, TransactionType.Debit, "AMAZON.COM", "Amazon", TransactionCategory.Shopping, D(10)),
      Txn(credit, 54.20m, TransactionType.Debit, "TARGET", "Target", TransactionCategory.Shopping, D(20)),
      Txn(credit, 32.00m, TransactionType.Debit, "AMC THEATRES", "AMC", TransactionCategory.Entertainment, D(16)),
      Txn(savings, 28.50m, TransactionType.Credit, "INTEREST PAYMENT", "BuoyAI Bank", TransactionCategory.OtherIncome, D(15)),
    };
  }

  private static BudgetCategory Cat(int budgetId, TransactionCategory category, decimal allocated, int sortOrder,
    bool isIncome = false, bool countUp = false, decimal rollover = 0m)
  {
    return new BudgetCategory
    {
      BudgetId = budgetId,
      CategoryId = (int)category,
      IsIncome = isIncome,
      AllocatedAmount = allocated,
      SortOrder = sortOrder,
      AllowRollover = true,
      RolloverBalance = rollover,
      IsCountUpEnabled = countUp,
      DailyAllowance = 0m,
      EarnedToDate = 0m,
      LastAllowanceCalculation = Now,
      CreatedAt = Now,
      UpdatedAt = Now
    };
  }

  /// <summary>
  /// Writes one prior-period snapshot (header + per-category rows) for a budget so the app's
  /// "budget history" view has previous-month data. Spent is approximated as allocated minus the
  /// current rollover balance (i.e. what was "used" last period), floored at zero.
  /// </summary>
  private static async Task SnapshotAsync(BuoyAIDbContext context, Budget budget,
    List<BudgetCategory> categories, DateTime periodStart, DateTime periodEnd)
  {
    decimal Spent(BudgetCategory c) => c.IsIncome ? 0m : Math.Max(0m, c.AllocatedAmount - c.RolloverBalance);

    var snapshot = new BudgetPeriodSnapshot
    {
      BudgetId = budget.Id,
      PeriodStart = periodStart,
      PeriodEnd = periodEnd,
      TotalAllocated = categories.Where(c => !c.IsIncome).Sum(c => c.AllocatedAmount),
      TotalSpent = categories.Sum(Spent),
      TotalIncome = categories.Where(c => c.IsIncome).Sum(c => c.AllocatedAmount),
      TotalRolloverIn = 0m,
      TotalRolloverOut = categories.Sum(c => c.RolloverBalance),
      CreatedAt = periodEnd
    };
    context.BudgetPeriodSnapshots.Add(snapshot);
    await context.SaveChangesAsync();

    var categorySnapshots = categories.Select(c => new BudgetPeriodCategorySnapshot
    {
      BudgetPeriodSnapshotId = snapshot.Id,
      BudgetCategoryId = c.Id,
      CategoryId = c.CategoryId ?? (int)TransactionCategory.Uncategorized,
      CategoryName = CategoryNames.GetValueOrDefault(c.CategoryId ?? (int)TransactionCategory.Uncategorized, "Uncategorized"),
      AllocatedAmount = c.AllocatedAmount,
      SpentAmount = Spent(c),
      RolloverIn = 0m,
      RolloverOut = c.RolloverBalance,
      AllowRollover = c.AllowRollover,
      IsIncome = c.IsIncome
    }).ToList();
    context.BudgetPeriodCategorySnapshots.AddRange(categorySnapshots);
    await context.SaveChangesAsync();
  }
}
