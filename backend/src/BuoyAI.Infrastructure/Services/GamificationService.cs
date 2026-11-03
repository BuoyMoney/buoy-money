using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Gamification;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

public class GamificationService : IGamificationService
{
  // Achievement threshold constants
  private const decimal SavingsHundredClub = 100m;
  private const decimal SavingsEmergencyFund = 500m;
  private const decimal SavingsMilestone = 1000m;
  private const decimal SavingsMilestone5k = 5000m;
  private const int StreakWeekDays = 7;
  private const int StreakMonthDays = 30;
  private const int StreakCenturyDays = 100;
  private const int StreakFreezeEarnEveryDays = 7; // earn a freeze each full week of streak
  private const int MaxStreakFreezes = 2;          // cap on banked freezes

  /// <summary>
  /// Activity streaks advanced by <see cref="RecordDailyActivityAsync"/>: they tick on a
  /// user action (dashboard open, manual categorization, savings contribution) rather than
  /// on a daily budget condition like under_budget. Same day/freeze/best-count semantics.
  /// </summary>
  private static readonly HashSet<string> ActivityStreakTypes = new()
  {
    "login",          // gamification dashboard opened
    "categorization", // user manually categorized a transaction
    "savings_goal"    // user added money to a savings goal
  };

  /// <summary>
  /// Categories that do NOT count as discretionary spending for the no_spend_day challenge:
  ///  - income (Salary/Bonus/Investment/OtherIncome) — money in, not spend (credits are
  ///    additionally excluded by the Type == Debit filter at the query site);
  ///  - bill-like fixed expenses the user can't reasonably skip for a day:
  ///    Rent, Utilities, Insurance, Subscriptions, LoanPayments;
  ///  - money movement that isn't consumption: CreditPayment, Transfer.
  /// Everything else — including Uncategorized, Other, and custom categories (ids 1000+) —
  /// DOES count as discretionary: unknown spend is still spend, and treating it leniently
  /// would let unreviewed transactions farm the reward.
  /// </summary>
  private static readonly int[] NoSpendDayExemptCategoryIds =
  {
    (int)TransactionCategory.Salary,
    (int)TransactionCategory.Bonus,
    (int)TransactionCategory.Investment,
    (int)TransactionCategory.OtherIncome,
    (int)TransactionCategory.Rent,
    (int)TransactionCategory.Utilities,
    (int)TransactionCategory.Insurance,
    (int)TransactionCategory.Subscriptions,
    (int)TransactionCategory.LoanPayments,
    (int)TransactionCategory.CreditPayment,
    (int)TransactionCategory.Transfer
  };

  private readonly BuoyAIDbContext _context;
  private readonly IBudgetSpendingService _spendingService;
  private readonly INotificationService _notificationService;
  private readonly IEmailService _emailService;
  private readonly ILogger<GamificationService> _logger;

  public GamificationService(
    BuoyAIDbContext context,
    IBudgetSpendingService spendingService,
    INotificationService notificationService,
    IEmailService emailService,
    ILogger<GamificationService> logger)
  {
    _context = context;
    _spendingService = spendingService;
    _notificationService = notificationService;
    _emailService = emailService;
    _logger = logger;
  }

  public async Task<GamificationDashboardDto> GetDashboardDataAsync(int userId)
  {
    // The dashboard load IS the daily touch: it advances the login streak and lazily
    // settles a no-spend-day challenge whose day has ended, so both resolve even for
    // users the 09:00 UTC daily reconciliation hasn't reached yet.
    await RecordDailyActivityAsync(userId, "login");
    await SettleNoSpendDayChallengeAsync(userId);

    var achievements = await _context.Set<UserAchievement>()
      .Where(a => a.UserId == userId && a.RevokedAt == null)
      .OrderByDescending(a => a.EarnedAt)
      .ToListAsync();

    var streaks = await _context.Set<UserStreak>()
      .Where(s => s.UserId == userId)
      .ToListAsync();

    var challenges = await _context.Set<UserChallenge>()
      .Where(c => c.UserId == userId && c.Status == "active")
      .OrderBy(c => c.EndDate)
      .ToListAsync();

    var totalPoints = achievements.Sum(a => a.Points);
    var (level, pointsToNext) = LevelDefinitions.GetLevel(totalPoints);

    return new GamificationDashboardDto
    {
      TotalPoints = totalPoints,
      Level = level,
      PointsToNextLevel = pointsToNext,
      LevelProgress = LevelDefinitions.GetProgress(totalPoints),
      AchievementCount = achievements.Count,
      ActiveStreaks = streaks.Select(MapToStreakDto).ToList(),
      RecentAchievements = achievements.Take(5).Select(MapToAchievementDto).ToList(),
      ActiveChallenges = challenges.Select(MapToChallengeDto).ToList(),
      NewAchievementIds = achievements.Where(a => a.IsNew).Select(a => a.AchievementKey).ToList()
    };
  }

  public async Task<List<UserAchievementDto>> GetAchievementsAsync(int userId)
  {
    var achievements = await _context.Set<UserAchievement>()
      .Where(a => a.UserId == userId && a.RevokedAt == null)
      .OrderByDescending(a => a.EarnedAt)
      .ToListAsync();

    return achievements.Select(MapToAchievementDto).ToList();
  }

  public async Task<List<AchievementCatalogItemDto>> GetAchievementCatalogAsync(int userId)
  {
    var earnedAt = (await _context.Set<UserAchievement>()
        .Where(a => a.UserId == userId && a.RevokedAt == null)
        .Select(a => new { a.AchievementKey, a.EarnedAt, a.TimesEarned })
        .ToListAsync())
      .GroupBy(a => a.AchievementKey)
      .ToDictionary(g => g.Key, g => (EarnedAt: g.Min(a => a.EarnedAt), Times: g.Max(a => a.TimesEarned)));

    // Rarity: share of users who have each achievement. "Population" is users who have any
    // achievement (avoids diluting rarity with brand-new/empty accounts); guard divide-by-zero.
    // Computed as DB aggregates (GROUP BY / COUNT DISTINCT) rather than loading the whole
    // UserAchievements table into memory. Per-key COUNT(*) == distinct earners because
    // (UserId, AchievementKey) is unique.
    var earnerCounts = await _context.Set<UserAchievement>()
      .Where(a => a.RevokedAt == null)
      .GroupBy(a => a.AchievementKey)
      .Select(g => new { Key = g.Key, Count = g.Count() })
      .ToDictionaryAsync(x => x.Key, x => x.Count);
    var population = await _context.Set<UserAchievement>()
      .Where(a => a.RevokedAt == null)
      .Select(a => a.UserId).Distinct().CountAsync();

    return AchievementDefinitions.All
      .Select(kvp =>
      {
        var key = kvp.Key;
        var def = kvp.Value;
        var isEarned = earnedAt.TryGetValue(key, out var when);
        var isNegative = AchievementDefinitions.IsNegative(key);
        // Good achievements stay a mystery until earned (client renders a silhouette);
        // cautionary ones are always legible so users can see what to avoid.
        var reveal = isEarned || isNegative;
        return new AchievementCatalogItemDto
        {
          Key = key,
          Title = reveal ? def.Title : null,
          Description = reveal ? def.Description : null,
          Icon = reveal ? def.Icon : null,
          Category = def.Category,
          Points = def.Points,
          Tier = AchievementDefinitions.TierFor(key),
          PercentEarned = population > 0 && earnerCounts.TryGetValue(key, out var c)
            ? Math.Round(c * 100.0 / population, 1)
            : 0,
          IsNegative = isNegative,
          IsEarned = isEarned,
          EarnedAt = isEarned ? when.EarnedAt : null,
          TimesEarned = isEarned ? when.Times : 0
        };
      })
      // Good before oops; earned before locked; then by points. The client sections on
      // IsNegative, but a stable order keeps the payload deterministic.
      .OrderBy(x => x.IsNegative)
      .ThenByDescending(x => x.IsEarned)
      .ThenBy(x => x.Points)
      .ToList();
  }

  public async Task AwardAsync(int userId, string achievementKey)
  {
    if (!AchievementDefinitions.All.ContainsKey(achievementKey))
    {
      _logger.LogWarning("AwardAsync called with unknown achievement key {Key}", achievementKey);
      return;
    }

    var existing = await _context.Set<UserAchievement>()
      .FirstOrDefaultAsync(a => a.UserId == userId && a.AchievementKey == achievementKey);

    var notify = new List<UserAchievement>();
    if (existing == null)
    {
      existing = CreateAchievement(userId, achievementKey);
      _context.Set<UserAchievement>().Add(existing);
      notify.Add(existing);
    }
    else if (existing.RevokedAt != null || AchievementDefinitions.Repeatable.Contains(achievementKey))
    {
      // Re-earn: repeatable badges count up; a revoked condition-badge un-revokes,
      // keeping its accumulated TimesEarned.
      if (AchievementDefinitions.Repeatable.Contains(achievementKey)) existing.TimesEarned++;
      existing.RevokedAt = null;
      existing.LastEarnedAt = DateTime.UtcNow;
      existing.IsNew = true;
      notify.Add(existing);
    }
    else
    {
      return; // single-award achievement already held
    }

    // Repeat-count meta-badge (e.g. Selective Amnesia x10 unlocks Chronic Amnesia).
    if (AchievementDefinitions.RepeatMeta.TryGetValue(achievementKey, out var meta)
        && existing.TimesEarned >= meta.Threshold)
    {
      var hasMeta = await _context.Set<UserAchievement>()
        .AnyAsync(a => a.UserId == userId && a.AchievementKey == meta.MetaKey);
      if (!hasMeta)
      {
        var metaAchievement = CreateAchievement(userId, meta.MetaKey);
        _context.Set<UserAchievement>().Add(metaAchievement);
        notify.Add(metaAchievement);
      }
    }

    await _context.SaveChangesAsync();
    _logger.LogInformation("Awarded achievement {Key} to user {UserId} (x{Times})",
      achievementKey, userId, existing.TimesEarned);
    await NotifyNewAchievementsAsync(userId, notify);
  }

  public async Task<List<OopsBadgeHistoryDto>> GetOopsBadgeHistoryAsync(int userId)
  {
    // Deliberately NO RevokedAt filter: a fixed (revoked) Time Traveler still carries
    // its repeat history, and that history is the point.
    var rows = await _context.Set<UserAchievement>()
      .Where(a => a.UserId == userId && AchievementDefinitions.Negative.Contains(a.AchievementKey))
      .ToListAsync();

    return rows
      .OrderByDescending(a => a.TimesEarned)
      .Select(a => new OopsBadgeHistoryDto
      {
        Key = a.AchievementKey,
        Title = a.Title,
        TimesEarned = a.TimesEarned,
        LastEarnedAt = a.LastEarnedAt ?? a.EarnedAt,
        CurrentlyHeld = a.RevokedAt == null
      })
      .ToList();
  }

  public async Task MarkAchievementsSeenAsync(int userId, List<string> achievementKeys)
  {
    var achievements = await _context.Set<UserAchievement>()
      .Where(a => a.UserId == userId && achievementKeys.Contains(a.AchievementKey) && a.IsNew)
      .ToListAsync();

    foreach (var achievement in achievements)
    {
      achievement.IsNew = false;
    }

    await _context.SaveChangesAsync();
  }

  public async Task<List<UserStreakDto>> GetStreaksAsync(int userId)
  {
    var streaks = await _context.Set<UserStreak>()
      .Where(s => s.UserId == userId)
      .ToListAsync();

    return streaks.Select(MapToStreakDto).ToList();
  }

  public async Task<List<UserChallengeDto>> GetActiveChallengesAsync(int userId)
  {
    var challenges = await _context.Set<UserChallenge>()
      .Where(c => c.UserId == userId && c.Status == "active")
      .OrderBy(c => c.EndDate)
      .ToListAsync();

    return challenges.Select(MapToChallengeDto).ToList();
  }

  public async Task DismissChallengeAsync(int userId, int challengeId)
  {
    var challenge = await _context.Set<UserChallenge>()
      .FirstOrDefaultAsync(c => c.Id == challengeId && c.UserId == userId);
    
    if (challenge != null)
    {
      _context.Set<UserChallenge>().Remove(challenge);
      await _context.SaveChangesAsync();
    }
  }

  public async Task CheckAndAwardAchievementsAsync(int userId)
  {
    var existingRows = await _context.Set<UserAchievement>()
      .Where(a => a.UserId == userId)
      .ToListAsync();
    // Active keys only: a revoked condition-badge must be re-earnable (it re-earns by
    // un-revoking its preserved row, keeping TimesEarned).
    var existingAchievements = existingRows
      .Where(a => a.RevokedAt == null)
      .Select(a => a.AchievementKey)
      .ToList();

    var newAchievements = new List<UserAchievement>();

    // Check for budget-related achievements
    if (!existingAchievements.Contains(AchievementDefinitions.FirstBudget))
    {
      var hasBudget = await _context.Set<Budget>().AnyAsync(b => b.UserId == userId);
      if (hasBudget)
      {
        newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.FirstBudget));
      }
    }

    // Check for savings goal achievements
    if (!existingAchievements.Contains(AchievementDefinitions.FirstSavingsGoal))
    {
      var hasSavingsGoal = await _context.Set<SavingsGoal>().AnyAsync(s => s.UserId == userId);
      if (hasSavingsGoal)
      {
        newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.FirstSavingsGoal));
      }
    }

    // Check for goal achieved
    if (!existingAchievements.Contains(AchievementDefinitions.GoalAchiever))
    {
      var hasCompletedGoal = await _context.Set<SavingsGoal>()
        .AnyAsync(s => s.UserId == userId && s.CurrentAmount >= s.TargetAmount);
      if (hasCompletedGoal)
      {
        newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.GoalAchiever));
      }
    }

    // Check for overachiever — saved 25%+ beyond a goal's target (overshooting is
    // intentionally allowed; this rewards it rather than capping it).
    if (!existingAchievements.Contains(AchievementDefinitions.Overachiever))
    {
      var hasOvershotGoal = await _context.Set<SavingsGoal>()
        .AnyAsync(s => s.UserId == userId && s.TargetAmount > 0 && s.CurrentAmount >= s.TargetAmount * 1.25m);
      if (hasOvershotGoal)
      {
        newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.Overachiever));
      }
    }

    // Check for savings milestone achievements
    var totalSavings = await _context.Set<SavingsGoal>()
      .Where(s => s.UserId == userId)
      .SumAsync(s => s.CurrentAmount);

    if (!existingAchievements.Contains(AchievementDefinitions.HundredClub) && totalSavings >= SavingsHundredClub)
    {
      newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.HundredClub));
    }

    if (!existingAchievements.Contains(AchievementDefinitions.EmergencyFundStarter) && totalSavings >= SavingsEmergencyFund)
    {
      newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.EmergencyFundStarter));
    }

    if (!existingAchievements.Contains(AchievementDefinitions.SavingsMilestone) && totalSavings >= SavingsMilestone)
    {
      newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.SavingsMilestone));
    }

    if (!existingAchievements.Contains(AchievementDefinitions.SavingsMilestone5k) && totalSavings >= SavingsMilestone5k)
    {
      newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.SavingsMilestone5k));
    }

    // Check for halfway there achievement (50% of any goal)
    if (!existingAchievements.Contains(AchievementDefinitions.HalfwayThere))
    {
      var hasHalfwayGoal = await _context.Set<SavingsGoal>()
        .AnyAsync(s => s.UserId == userId && s.TargetAmount > 0 && s.CurrentAmount >= s.TargetAmount * 0.5m);
      if (hasHalfwayGoal)
      {
        newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.HalfwayThere));
      }
    }

    // Check for budget-history achievements (snapshot-based, permanent)
    await CheckBudgetHistoryAchievementsAsync(userId, existingAchievements, newAchievements);

    // Condition-based (revocable) achievements — the count-up family + "Time Traveler".
    // These reflect *where current spending lands*, so they are reconciled against live state:
    // awarded when the condition holds, and REVOKED when a later change (e.g. recategorizing a
    // transaction) makes it stop holding. Milestones, streak counts, and closed-period awards
    // stay permanent.
    var conditions = await ComputeCountUpConditionsAsync(userId);
    conditions[AchievementDefinitions.TimeTraveler] = await _context.Set<BudgetCategory>()
      .AnyAsync(c => c.Budget.UserId == userId
        && !c.IsIncome
        && c.AllowRollover
        && c.RolloverBalance < 0
        && c.AllocatedAmount + c.RolloverBalance <= 0);

    var revocations = new List<string>();
    var reEarned = new List<UserAchievement>();
    foreach (var key in AchievementDefinitions.Revocable)
    {
      var holds = conditions.GetValueOrDefault(key, false);
      var row = existingRows.FirstOrDefault(a => a.AchievementKey == key);
      if (holds && row == null)
      {
        newAchievements.Add(CreateAchievement(userId, key));
      }
      else if (holds && row != null && row.RevokedAt != null)
      {
        // Re-earn by un-revoking the preserved row so TimesEarned keeps its history.
        row.RevokedAt = null;
        row.LastEarnedAt = DateTime.UtcNow;
        row.IsNew = true;
        if (AchievementDefinitions.Repeatable.Contains(key)) row.TimesEarned++;
        reEarned.Add(row);
        if (AchievementDefinitions.RepeatMeta.TryGetValue(key, out var meta)
            && row.TimesEarned >= meta.Threshold
            && !existingRows.Any(a => a.AchievementKey == meta.MetaKey)
            && !newAchievements.Any(a => a.AchievementKey == meta.MetaKey))
        {
          newAchievements.Add(CreateAchievement(userId, meta.MetaKey));
        }
      }
      else if (!holds && row != null && row.RevokedAt == null)
      {
        revocations.Add(key);
      }
    }

    // Check for quiz completion
    if (!existingAchievements.Contains(AchievementDefinitions.QuizComplete))
    {
      var hasQuiz = await _context.Set<UserOnboardingQuiz>().AnyAsync(q => q.UserId == userId);
      if (hasQuiz)
      {
        newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.QuizComplete));
      }
    }

    // Check for linked accounts
    if (!existingAchievements.Contains(AchievementDefinitions.AllAccountsLinked))
    {
      var accountCount = await _context.Set<Account>().CountAsync(a => a.UserId == userId);
      if (accountCount >= 3)
      {
        newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.AllAccountsLinked));
      }
    }

    // Check for streak achievements
    var currentStreaks = await _context.Set<UserStreak>()
      .Where(s => s.UserId == userId && s.StreakType == "under_budget")
      .FirstOrDefaultAsync();

    if (currentStreaks != null)
    {
      if (currentStreaks.CurrentCount >= StreakWeekDays && !existingAchievements.Contains(AchievementDefinitions.WeekStreak))
      {
        newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.WeekStreak));
      }
      if (currentStreaks.CurrentCount >= StreakMonthDays && !existingAchievements.Contains(AchievementDefinitions.MonthStreak))
      {
        newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.MonthStreak));
      }
      if (currentStreaks.CurrentCount >= StreakCenturyDays && !existingAchievements.Contains(AchievementDefinitions.CenturyClub))
      {
        newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.CenturyClub));
      }
    }

    if (newAchievements.Any())
    {
      _context.Set<UserAchievement>().AddRange(newAchievements);
    }
    if (revocations.Count > 0)
    {
      // Flag instead of delete so TimesEarned survives for the next earn.
      foreach (var row in existingRows.Where(a =>
          revocations.Contains(a.AchievementKey) && a.RevokedAt == null))
      {
        row.RevokedAt = DateTime.UtcNow;
      }
    }

    if (newAchievements.Any() || revocations.Count > 0 || reEarned.Count > 0)
    {
      await _context.SaveChangesAsync();
      var toNotify = newAchievements.Concat(reEarned).ToList();
      if (toNotify.Any())
      {
        _logger.LogInformation("Awarded {Count} new/re-earned achievements to user {UserId}", toNotify.Count, userId);
        await NotifyNewAchievementsAsync(userId, toNotify);
      }
      if (revocations.Count > 0)
      {
        _logger.LogInformation("Revoked {Count} achievements from user {UserId} (condition no longer holds): {Keys}",
          revocations.Count, userId, string.Join(",", revocations));
      }
    }
  }

  /// <summary>
  /// Fire in-app notifications for newly-earned achievements and (respecting prefs) a
  /// single digest email for the batch. Best-effort: never let a notification failure
  /// break the award itself.
  /// </summary>
  private async Task NotifyNewAchievementsAsync(int userId, List<UserAchievement> earned)
  {
    if (earned.Count == 0) return;
    try
    {
      foreach (var a in earned)
      {
        await _notificationService.CreateNotificationAsync(userId, new CreateNotificationRequest
        {
          Type = "achievement",
          Title = a.Category == "oops" ? "Oops — badge earned" : "Achievement unlocked!",
          Message = $"{a.Icon} {a.Title} — {a.Description}",
          // In-app route, NOT the "/app"-prefixed web deep link (that prefix is the web base-href
          // and the email templates' concern). Every other notification ActionUrl is unprefixed;
          // "/app/achievements" 404'd because GoRouter's route is "/achievements".
          ActionUrl = "/achievements",
          ActionText = "View"
        });
      }

      // One email for the whole batch (avoid inbox spam), gated on the badge-earned pref.
      var top = earned.OrderByDescending(a => a.Points).First();
      var prefs = await _context.Set<UserEmailPreferences>()
        .FirstOrDefaultAsync(p => p.UserId == userId);
      if (prefs == null || prefs.BadgeEarned)
      {
        var user = await _context.Set<User>().FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null && !string.IsNullOrWhiteSpace(user.Email))
        {
          var name = string.IsNullOrWhiteSpace(user.FirstName) ? "there" : user.FirstName;
          var extra = earned.Count > 1 ? $" (+{earned.Count - 1} more)" : "";
          await _emailService.SendBadgeEarnedAsync(
            user.Email, name, top.Title + extra, top.Description, top.Icon);
        }
      }
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to send achievement notifications for user {UserId}", userId);
    }
  }

  public async Task UpdateStreaksAsync(int userId)
  {
    var today = DateTime.UtcNow.Date;

    // Personal under-budget streak, driven by the user's own active budget.
    var personalBudget = await _context.Set<Budget>()
      .Include(b => b.Categories)
      .FirstOrDefaultAsync(b => b.UserId == userId && b.IsActive);
    if (personalBudget != null)
    {
      await ApplyBudgetStreakDayAsync(userId, "under_budget", personalBudget, today);
    }

    // Shared family streak, driven by the family's primary budget. Every member's row is
    // updated identically (the daily reconciliation keeps absent members' rows in sync), so
    // the whole household sees the same shared streak.
    var user = await _context.Set<User>().FirstOrDefaultAsync(u => u.Id == userId);
    if (user?.FamilyId != null)
    {
      var familyBudget = await _context.Set<Budget>()
        .Include(b => b.Categories)
        .FirstOrDefaultAsync(b => b.FamilyId == user.FamilyId && b.IsActive && b.IsPrimary);
      if (familyBudget != null)
      {
        await ApplyBudgetStreakDayAsync(userId, "family_under_budget", familyBudget, today);
      }
    }

    // Daily reconciliation hook: this method runs for every active user via
    // GamificationDailyService → GamificationEvaluationService, so a finished
    // no-spend-day challenge is settled here even if the user never opens the app.
    await SettleNoSpendDayChallengeAsync(userId);
  }

  /// <summary>
  /// Advances (or resets) a personal activity streak — login, categorization, savings_goal —
  /// for "today". At most one increment per UTC day; follows the exact under_budget
  /// conventions (LastIncrementDate day-compare, freeze consumption, weekly freeze banking,
  /// BestCount update). Unknown streak types are ignored with a warning.
  /// </summary>
  public async Task RecordDailyActivityAsync(int userId, string streakType)
  {
    if (!ActivityStreakTypes.Contains(streakType))
    {
      _logger.LogWarning("RecordDailyActivityAsync called with unknown streak type {Type}", streakType);
      return;
    }

    // Gamification is a bonus on top of the host operation (a transfer, a
    // categorization, a dashboard load) — it must never fail it. In particular
    // a first-ever streak insert can lose a (UserId, StreakType) unique-index
    // race under concurrency; swallow and log rather than 500 a committed op.
    try
    {
      var today = DateTime.UtcNow.Date;
      var streak = await GetOrCreateStreakAsync(userId, streakType, today);
      var isNew = streak.Id == 0;
      var advanced = AdvanceStreakDay(streak, today);
      if (!advanced && !isNew)
      {
        return; // already counted today — no dirty write on every page load
      }
      streak.UpdatedAt = DateTime.UtcNow;
      await _context.SaveChangesAsync();
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to record {Type} activity for user {UserId}", streakType, userId);
    }
  }

  /// <summary>
  /// Advances (or resets) a user's streak of the given type for "today", based on whether
  /// <paramref name="budget"/> is under its allocation. Shared by the personal and family
  /// streaks. Freeze/grace + weekly-earn semantics live here.
  /// </summary>
  private async Task ApplyBudgetStreakDayAsync(
    int userId, string streakType, Budget budget, DateTime today)
  {
    var streak = await GetOrCreateStreakAsync(userId, streakType, today);

    var categorySpending = await _spendingService.GetCategorySpendingAsync(budget.Id);
    var totalSpent = categorySpending.Values.Sum();
    var totalAllocated = budget.Categories.Where(c => !c.IsIncome).Sum(c => c.AllocatedAmount);
    if (totalSpent > totalAllocated)
    {
      // Not under budget today — nothing to advance. If GetOrCreate just Added a
      // brand-new row, detach it so a later unrelated SaveChanges in this scope
      // doesn't flush a phantom zero-count streak.
      if (streak.Id == 0) _context.Entry(streak).State = EntityState.Detached;
      return;
    }

    AdvanceStreakDay(streak, today);

    streak.UpdatedAt = DateTime.UtcNow;
    await _context.SaveChangesAsync();
  }

  private async Task<UserStreak> GetOrCreateStreakAsync(int userId, string streakType, DateTime today)
  {
    var streak = await _context.Set<UserStreak>()
      .FirstOrDefaultAsync(s => s.UserId == userId && s.StreakType == streakType);
    if (streak == null)
    {
      streak = new UserStreak
      {
        UserId = userId,
        StreakType = streakType,
        CurrentCount = 0,
        BestCount = 0,
        LastIncrementDate = today.AddDays(-1),
        StreakStartDate = today,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
      };
      _context.Set<UserStreak>().Add(streak);
    }
    return streak;
  }

  /// <summary>
  /// Shared day-advance core for every streak type: no-op when today is already counted,
  /// +1 on a consecutive day, banked freezes cover a gap (else reset to 1), a freeze is
  /// earned each full week (capped), and BestCount tracks the high-water mark.
  /// </summary>
  private static bool AdvanceStreakDay(UserStreak streak, DateTime today)
  {
    var last = streak.LastIncrementDate.Date;
    var gapDays = (today - last).Days;

    if (gapDays <= 0)
    {
      // Already counted today — never double-increment.
      return false;
    }
    else if (gapDays == 1)
    {
      streak.CurrentCount++;
      streak.LastIncrementDate = today;
    }
    else
    {
      // A gap: cover missed day(s) with banked freezes, else break and restart.
      var missed = gapDays - 1;
      if (streak.StreakFreezes >= missed)
      {
        streak.StreakFreezes -= missed;
        streak.CurrentCount++;
        streak.LastIncrementDate = today;
      }
      else
      {
        streak.CurrentCount = 1;
        streak.StreakStartDate = today;
        streak.LastIncrementDate = today;
        streak.StreakFreezes = 0;
      }
    }

    // Earn a freeze each full week of streak, capped.
    if (streak.CurrentCount > 0 && streak.CurrentCount % StreakFreezeEarnEveryDays == 0
        && streak.StreakFreezes < MaxStreakFreezes)
    {
      streak.StreakFreezes++;
    }

    if (streak.CurrentCount > streak.BestCount)
    {
      streak.BestCount = streak.CurrentCount;
    }

    return true;
  }

  /// <summary>
  /// Settles an active no_spend_day challenge once its day has fully ended: completed when
  /// the user recorded zero discretionary expense transactions on the challenge day
  /// (see <see cref="NoSpendDayExemptCategoryIds"/> for what doesn't count), expired
  /// otherwise. Called from the daily reconciliation (UpdateStreaksAsync, reached for every
  /// active user via GamificationDailyService) and lazily on dashboard load.
  /// </summary>
  private async Task SettleNoSpendDayChallengeAsync(int userId)
  {
    var now = DateTime.UtcNow;
    var challenge = await _context.Set<UserChallenge>()
      .FirstOrDefaultAsync(c => c.UserId == userId
        && c.ChallengeType == "no_spend_day"
        && c.Status == "active");
    if (challenge == null || now <= challenge.EndDate) return; // no challenge, or day not over yet

    var dayStart = challenge.StartDate.Date;
    var dayEnd = dayStart.AddDays(1);

    // Only debits count (credits are refunds/income); pending debits still count — they are
    // spend the user chose to make on that day.
    var hadDiscretionarySpend = await _context.Set<Transaction>()
      .AnyAsync(t => t.Account.UserId == userId
        && t.Date >= dayStart && t.Date < dayEnd
        && t.Type == TransactionType.Debit
        && (t.CategoryId == null || !NoSpendDayExemptCategoryIds.Contains(t.CategoryId.Value)));

    if (!hadDiscretionarySpend)
    {
      challenge.CurrentValue = challenge.TargetValue;
      challenge.Status = "completed";
      challenge.CompletedAt = now;
      _logger.LogInformation(
        "User {UserId} completed no_spend_day challenge {ChallengeId}", userId, challenge.Id);
    }
    else
    {
      // The day ended with discretionary spend — the challenge can never complete, so
      // expire it rather than leaving a stale "active" row to be re-scanned forever.
      challenge.Status = "expired";
    }

    await _context.SaveChangesAsync();
  }

  public async Task IncrementChallengeProgressAsync(int userId, string challengeType, decimal amount = 1)
  {
    var challenge = await _context.Set<UserChallenge>()
      .FirstOrDefaultAsync(c => c.UserId == userId
        && c.ChallengeType == challengeType
        && c.Status == "active"
        && c.EndDate > DateTime.UtcNow);
    if (challenge == null) return;

    challenge.CurrentValue = Math.Min(challenge.TargetValue, challenge.CurrentValue + amount);
    if (challenge.CurrentValue >= challenge.TargetValue)
    {
      challenge.Status = "completed";
      _logger.LogInformation(
        "User {UserId} completed challenge {Type}", userId, challengeType);
    }
    await _context.SaveChangesAsync();
  }

  public async Task<UserChallengeDto?> GenerateDailyChallengeAsync(int userId)
  {
    // Check if user already has an active challenge
    var existingChallenge = await _context.Set<UserChallenge>()
      .FirstOrDefaultAsync(c => c.UserId == userId && c.Status == "active" && c.EndDate > DateTime.UtcNow);

    if (existingChallenge != null)
    {
      return MapToChallengeDto(existingChallenge);
    }

    // Generate a simple daily challenge. The first two advance via
    // IncrementChallengeProgressAsync from the actions the user actually takes;
    // no_spend_day is settled after its day ends by SettleNoSpendDayChallengeAsync
    // (daily reconciliation + lazy dashboard-load check).
    var challenges = new[]
    {
      ("categorize_transactions", "Categorization Champ", "Categorize 5 transactions", 5m, 25),
      ("review_budget", "Budget Check", "Review your budget categories", 1m, 15),
      ("no_spend_day", "No-Spend Day", "Get through today without any discretionary spending", 1m, 30)
    };

    var (type, title, description, target, points) = challenges[Random.Shared.Next(challenges.Length)];

    var newChallenge = new UserChallenge
    {
      UserId = userId,
      ChallengeType = type,
      Title = title,
      Description = description,
      TargetValue = target,
      CurrentValue = 0,
      RewardPoints = points,
      StartDate = DateTime.UtcNow,
      EndDate = DateTime.UtcNow.Date.AddDays(1).AddSeconds(-1),
      Status = "active",
      CreatedAt = DateTime.UtcNow
    };

    _context.Set<UserChallenge>().Add(newChallenge);
    await _context.SaveChangesAsync();

    return MapToChallengeDto(newChallenge);
  }

  /// <summary>
  /// Budget-history achievements computed from closed-period snapshots:
  /// Budget Master (3 consecutive periods finishing under budget) and Category Champion
  /// (every expense category under its allocation in the most recent closed period).
  /// </summary>
  private async Task CheckBudgetHistoryAchievementsAsync(
    int userId,
    List<string> existingAchievements,
    List<UserAchievement> newAchievements)
  {
    var needsMaster = !existingAchievements.Contains(AchievementDefinitions.BudgetMaster);
    var needsChampion = !existingAchievements.Contains(AchievementDefinitions.CategoryChampion);
    if (!needsMaster && !needsChampion) return;

    // Budget Master: a run of 3 consecutive closed periods finishing at/under budget.
    if (needsMaster)
    {
      var periods = await _context.Set<BudgetPeriodSnapshot>()
        .Where(s => s.Budget.UserId == userId && s.TotalAllocated > 0)
        .OrderBy(s => s.PeriodStart)
        .Select(s => new { s.TotalAllocated, s.TotalSpent })
        .ToListAsync();

      var run = 0;
      foreach (var p in periods)
      {
        run = p.TotalSpent <= p.TotalAllocated ? run + 1 : 0;
        if (run >= 3)
        {
          newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.BudgetMaster));
          break;
        }
      }
    }

    // Category Champion: in the most recent closed period, every budgeted expense category
    // finished at/under its allocation.
    if (needsChampion)
    {
      var latestSnapshotId = await _context.Set<BudgetPeriodSnapshot>()
        .Where(s => s.Budget.UserId == userId)
        .OrderByDescending(s => s.PeriodStart)
        .Select(s => s.Id)
        .FirstOrDefaultAsync();

      if (latestSnapshotId != 0)
      {
        var categories = await _context.Set<BudgetPeriodCategorySnapshot>()
          .Where(c => c.BudgetPeriodSnapshotId == latestSnapshotId
            && !c.IsIncome
            && c.AllocatedAmount > 0)
          .Select(c => new { c.AllocatedAmount, c.SpentAmount })
          .ToListAsync();

        if (categories.Count > 0 && categories.All(c => c.SpentAmount <= c.AllocatedAmount))
        {
          newAchievements.Add(CreateAchievement(userId, AchievementDefinitions.CategoryChampion));
        }
      }
    }
  }

  /// <summary>
  /// Computes the CURRENT truth of each count-up achievement condition (does not award). All
  /// false when the user has no count-up categories — which lets the reconciler revoke any
  /// previously-earned count-up badge once the spending that earned it moves elsewhere.
  /// </summary>
  private async Task<Dictionary<string, bool>> ComputeCountUpConditionsAsync(int userId)
  {
    var conditions = new Dictionary<string, bool>
    {
      [AchievementDefinitions.CountUpChampion] = false,
      [AchievementDefinitions.SelfCareHero] = false,
      [AchievementDefinitions.GymRegular] = false,
      [AchievementDefinitions.BalancedBudgeter] = false,
    };

    var budgets = await _context.Set<Budget>()
      .Include(b => b.Categories)
      .Where(b => b.UserId == userId)
      .ToListAsync();

    var countUpCategories = budgets
      .SelectMany(b => b.Categories)
      .Where(c => c.IsCountUpEnabled)
      .ToList();

    if (!countUpCategories.Any()) return conditions;

    // Build a spending lookup for all categories across all budgets
    var categorySpending = new Dictionary<int, decimal>();
    foreach (var budget in budgets)
    {
      var budgetSpending = await _spendingService.GetCategorySpendingAsync(budget.Id);
      foreach (var (categoryId, spent) in budgetSpending)
      {
        categorySpending[categoryId] = spent;
      }
    }

    decimal GetSpent(BudgetCategory c) => categorySpending.GetValueOrDefault(c.Id, 0);

    // Count-Up Champion: hit any count-up target.
    conditions[AchievementDefinitions.CountUpChampion] =
      countUpCategories.Any(c => GetSpent(c) >= c.AllocatedAmount && c.AllocatedAmount > 0);

    // Self-Care Hero: PersonalCare/Healthcare count-up at 80%+.
    conditions[AchievementDefinitions.SelfCareHero] = countUpCategories
      .Where(c => c.CategoryId == (int)TransactionCategory.PersonalCare ||
                  c.CategoryId == (int)TransactionCategory.Healthcare)
      .Any(c => c.AllocatedAmount > 0 && GetSpent(c) >= c.AllocatedAmount * 0.8m);

    // Gym Regular: Entertainment/PersonalCare count-up hit (proxy for fitness).
    conditions[AchievementDefinitions.GymRegular] = countUpCategories
      .Where(c => c.CategoryId == (int)TransactionCategory.Entertainment ||
                  c.CategoryId == (int)TransactionCategory.PersonalCare)
      .Any(c => c.AllocatedAmount > 0 && GetSpent(c) >= c.AllocatedAmount);

    // Balanced Budgeter: 3+ count-up budgets, all within 90-110% of target.
    var activeCountUps = countUpCategories.Where(c => c.AllocatedAmount > 0).ToList();
    conditions[AchievementDefinitions.BalancedBudgeter] = activeCountUps.Count >= 3
      && activeCountUps.All(c =>
      {
        var percentage = GetSpent(c) / c.AllocatedAmount * 100;
        return percentage >= 90 && percentage <= 110;
      });

    return conditions;
  }

  private static UserAchievement CreateAchievement(int userId, string key)
  {
    var def = AchievementDefinitions.All[key];
    return new UserAchievement
    {
      UserId = userId,
      AchievementKey = key,
      Title = def.Title,
      Description = def.Description,
      Icon = def.Icon,
      Category = def.Category,
      Points = def.Points,
      EarnedAt = DateTime.UtcNow,
      LastEarnedAt = DateTime.UtcNow,
      IsNew = true
    };
  }

  private static UserAchievementDto MapToAchievementDto(UserAchievement a) => new()
  {
    Id = a.Id,
    AchievementKey = a.AchievementKey,
    Title = a.Title,
    Description = a.Description,
    Icon = a.Icon,
    Category = a.Category,
    Points = a.Points,
    EarnedAt = a.EarnedAt,
    IsNew = a.IsNew,
    TimesEarned = a.TimesEarned
  };

  private static UserStreakDto MapToStreakDto(UserStreak s)
  {
    var displayName = s.StreakType switch
    {
      "under_budget" => "Under Budget Streak",
      "family_under_budget" => "Family Streak",
      "savings_goal" => "Savings Streak",
      "login" => "Daily Login",
      "categorization" => "Categorization Streak",
      _ => s.StreakType
    };

    var icon = s.StreakType switch
    {
      "under_budget" => "🔥",
      "family_under_budget" => "👨‍👩‍👧",
      "savings_goal" => "💰",
      "login" => "📅",
      "categorization" => "🏷️",
      _ => "⭐"
    };

    var isAtRisk = s.LastIncrementDate.Date < DateTime.UtcNow.Date;

    return new UserStreakDto
    {
      Id = s.Id,
      StreakType = s.StreakType,
      DisplayName = displayName,
      Icon = icon,
      CurrentCount = s.CurrentCount,
      BestCount = s.BestCount,
      LastIncrementDate = s.LastIncrementDate,
      StreakStartDate = s.StreakStartDate,
      IsAtRisk = isAtRisk,
      StreakFreezes = s.StreakFreezes
    };
  }

  private static UserChallengeDto MapToChallengeDto(UserChallenge c) => new()
  {
    Id = c.Id,
    ChallengeType = c.ChallengeType,
    Title = c.Title,
    Description = c.Description,
    TargetValue = c.TargetValue,
    CurrentValue = c.CurrentValue,
    RewardPoints = c.RewardPoints,
    StartDate = c.StartDate,
    EndDate = c.EndDate,
    Status = c.Status
  };
}
