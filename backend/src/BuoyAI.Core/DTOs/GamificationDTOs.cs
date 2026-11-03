namespace BuoyAI.Core.DTOs.Gamification;

/// <summary>
/// Achievement DTO for API responses
/// </summary>
public class UserAchievementDto
{
  public int TimesEarned { get; set; } = 1;
  public int Id { get; set; }
  public string AchievementKey { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public string Icon { get; set; } = string.Empty;
  public string Category { get; set; } = string.Empty;
  public int Points { get; set; }
  public DateTime EarnedAt { get; set; }
  public bool IsNew { get; set; }
}

/// <summary>
/// A single entry in the full achievement catalog (every definition merged with the
/// caller's earned state). Powers the Achievements page.
///
/// "Good" achievements stay a mystery until earned: when <see cref="IsEarned"/> is
/// false and <see cref="IsNegative"/> is false, <see cref="Title"/>,
/// <see cref="Description"/> and <see cref="Icon"/> are null so the client can render a
/// locked silhouette without the payload leaking what the badge is. Cautionary
/// ("oops") achievements are always fully described, earned or not.
/// </summary>
public class AchievementCatalogItemDto
{
  public int TimesEarned { get; set; }
  public string Key { get; set; } = string.Empty;
  public string? Title { get; set; }
  public string? Description { get; set; }
  public string? Icon { get; set; }
  public string Category { get; set; } = string.Empty;
  public int Points { get; set; }
  /// <summary>Bronze/Silver/Gold/Platinum for "good" achievements; null for cautionary ones.</summary>
  public string? Tier { get; set; }
  /// <summary>Share of active users who have earned this (0–100). Drives the rarity label.</summary>
  public double PercentEarned { get; set; }
  public bool IsNegative { get; set; }
  public bool IsEarned { get; set; }
  public DateTime? EarnedAt { get; set; }
}

/// <summary>
/// Streak DTO for API responses
/// </summary>
public class UserStreakDto
{
  public int Id { get; set; }
  public string StreakType { get; set; } = string.Empty;
  public string DisplayName { get; set; } = string.Empty;
  public string Icon { get; set; } = string.Empty;
  public int CurrentCount { get; set; }
  public int BestCount { get; set; }
  public DateTime LastIncrementDate { get; set; }
  public DateTime StreakStartDate { get; set; }
  public bool IsAtRisk { get; set; }  // True if streak might break soon
  public int StreakFreezes { get; set; }  // Banked freezes that auto-cover a missed day
}

/// <summary>
/// Challenge DTO for API responses
/// </summary>
public class UserChallengeDto
{
  public int Id { get; set; }
  public string ChallengeType { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public decimal TargetValue { get; set; }
  public decimal CurrentValue { get; set; }
  public decimal ProgressPercentage => TargetValue > 0 ? Math.Min(100, CurrentValue / TargetValue * 100) : 0;
  public int RewardPoints { get; set; }
  public DateTime StartDate { get; set; }
  public DateTime EndDate { get; set; }
  public string Status { get; set; } = string.Empty;
  public int DaysRemaining => Math.Max(0, (EndDate.Date - DateTime.UtcNow.Date).Days);
}

/// <summary>
/// Dashboard gamification data
/// </summary>
public class GamificationDashboardDto
{
  public int TotalPoints { get; set; }
  public string Level { get; set; } = "Beginner";
  public int PointsToNextLevel { get; set; }
  /// <summary>Progress through the CURRENT level, 0.0–1.0 (1.0 at max level). Lets the
  /// client render a real progress bar instead of a placeholder.</summary>
  public double LevelProgress { get; set; }
  public int AchievementCount { get; set; }
  public List<UserStreakDto> ActiveStreaks { get; set; } = new();
  public List<UserAchievementDto> RecentAchievements { get; set; } = new();
  public List<UserChallengeDto> ActiveChallenges { get; set; } = new();
  public List<string> NewAchievementIds { get; set; } = new();  // Achievements to animate
}

/// <summary>
/// Visualization specification for AI-generated charts
/// </summary>
public class VisualizationSpec
{
  /// <summary>
  /// Chart type: pie, bar, line, area, progress, streak, gauge
  /// </summary>
  public string Type { get; set; } = string.Empty;
  
  /// <summary>
  /// Chart data points
  /// </summary>
  public List<ChartDataPoint> Data { get; set; } = new();
  
  /// <summary>
  /// Chart title
  /// </summary>
  public string? Title { get; set; }
  
  /// <summary>
  /// Personalized motivational message based on user's quiz profile
  /// </summary>
  public string? MotivationalMessage { get; set; }
  
  /// <summary>
  /// Additional configuration options
  /// </summary>
  public ChartConfig? Config { get; set; }
}

public class ChartDataPoint
{
  public string Name { get; set; } = string.Empty;
  public decimal Value { get; set; }
  public string? Color { get; set; }
  public string? Label { get; set; }
}

public class ChartConfig
{
  public string? XAxisLabel { get; set; }
  public string? YAxisLabel { get; set; }
  public bool ShowLegend { get; set; } = true;
  public bool Animate { get; set; } = true;
  public string? ValuePrefix { get; set; }  // e.g., "$"
  public string? ValueSuffix { get; set; }  // e.g., "%"
}

/// <summary>
/// Extended AI Insight with visualization support
/// </summary>
public class AIInsightWithVisualizationDto
{
  public string Id { get; set; } = string.Empty;
  public string Type { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
  public string Message { get; set; } = string.Empty;
  public string? ActionSuggestion { get; set; }
  public double Confidence { get; set; }
  public string Priority { get; set; } = string.Empty;
  public DateTime GeneratedAt { get; set; }
  
  /// <summary>
  /// Optional visualization for this insight
  /// </summary>
  public VisualizationSpec? Visualization { get; set; }
}

/// <summary>
/// Predefined achievement definitions
/// </summary>
public static class AchievementDefinitions
{
  // Budget achievements
  public const string FirstBudget = "first_budget";
  public const string BudgetMaster = "budget_master";  // 3 months under budget
  public const string CategoryChampion = "category_champion";  // All categories under budget
  public const string SelectiveAmnesia = "selective_amnesia";  // Reset negative rollover debt
  
  // Savings achievements
  public const string FirstSavingsGoal = "first_savings_goal";
  public const string GoalAchiever = "goal_achiever";  // Complete a savings goal
  public const string Overachiever = "overachiever";  // Exceed a savings goal by 25%+
  public const string HundredClub = "hundred_club";  // First $100 saved
  public const string EmergencyFundStarter = "emergency_fund_starter";  // $500 saved
  public const string HalfwayThere = "halfway_there";  // 50% of any goal
  public const string SavingsMilestone = "savings_milestone";  // $1000 total saved
  public const string SavingsMilestone5k = "savings_milestone_5k";  // $5000 total saved
  
  // Count-Up Budget achievements
  public const string CountUpChampion = "count_up_champion";  // Hit count-up target
  public const string SelfCareHero = "self_care_hero";  // Used self-care budget consistently
  public const string GymRegular = "gym_regular";  // Hit gym/fitness count-up 4x in a month
  public const string BalancedBudgeter = "balanced_budgeter";  // All count-up budgets 90-110% of target
  
  // Streak achievements
  public const string WeekStreak = "week_streak";  // 7 day under budget
  public const string MonthStreak = "month_streak";  // 30 day streak
  public const string CenturyClub = "century_club";  // 100 day streak

  // Engagement achievements
  public const string QuizComplete = "quiz_complete";
  public const string AllAccountsLinked = "all_accounts_linked";
  // Two badges, not one, because the referral loop has two distinct moments and only the
  // second one is worth anything. Connector fires when a friend signs up with your code —
  // real, immediate, but no reward attached. Recruiter fires when that friend actually
  // connects an account and the reward is granted. One badge covering both would either
  // celebrate too early (a signup that may never activate) or leave the invite unrecognised.
  public const string Connector = "connector";  // A friend signed up with your code
  public const string Recruiter = "recruiter";  // ...and completed setup, so the reward paid

  // Cautionary ("oops") achievements — earned for slip-ups, not wins. They carry
  // no points and are surfaced separately (with an explanatory tooltip, not a
  // locked silhouette) so users can see what they mean without having to earn them.
  public const string TimeTraveler = "time_traveler";  // Overspent into next month's budget
  public const string ChronicAmnesia = "chronic_amnesia";  // Selective Amnesia x10
  public const string TimeLord = "time_lord";              // Time Traveler x10

  /// <summary>
  /// Keys that represent cautionary ("bad") achievements rather than wins. Drives
  /// the separate section on the achievements page and keeps them worth 0 points.
  /// </summary>
  public static readonly HashSet<string> Negative = new()
  {
    TimeTraveler, SelectiveAmnesia, ChronicAmnesia, TimeLord
  };

  /// <summary>
  /// Achievements that can be earned repeatedly; each earn increments TimesEarned.
  /// Deliberately just the cautionary badges: wins stay one-and-done.
  /// </summary>
  public static readonly HashSet<string> Repeatable = new() { TimeTraveler, SelectiveAmnesia };

  /// <summary>Meta-badges unlocked by earning a repeatable badge N times.</summary>
  public static readonly Dictionary<string, (string MetaKey, int Threshold)> RepeatMeta = new()
  {
    [SelectiveAmnesia] = (ChronicAmnesia, 10),
    [TimeTraveler] = (TimeLord, 10),
  };

  public static bool IsNegative(string key) => Negative.Contains(key);

  /// <summary>
  /// Condition-based achievements that reflect *current* state (where spending lands), so they
  /// are re-evaluated on changes and REVOKED if their condition stops holding — e.g. after a
  /// recategorization fixes an overspend or moves spend out of a count-up category. Milestones
  /// ($ saved, goals), earned streak counts, and closed-period snapshot awards are NOT here —
  /// those stay permanent once earned. Note: selective_amnesia is a one-off event (debt reset)
  /// with no re-derivable condition, so it is intentionally excluded.
  /// </summary>
  public static readonly HashSet<string> Revocable = new()
  {
    TimeTraveler, CountUpChampion, SelfCareHero, GymRegular, BalancedBudgeter
  };

  /// <summary>
  /// Rarity tier for a "good" achievement, derived from its point weight (points already
  /// encode difficulty). Returns null for cautionary ("oops") achievements, which aren't wins.
  /// </summary>
  public static string? TierFor(string key)
  {
    if (IsNegative(key) || !All.TryGetValue(key, out var def)) return null;
    var points = def.Points;
    if (points >= 500) return "Platinum";
    if (points >= 250) return "Gold";
    if (points >= 100) return "Silver";
    return "Bronze";
  }

  public static Dictionary<string, (string Title, string Description, string Icon, string Category, int Points)> All => new()
  {
    // Budget achievements
    [FirstBudget] = ("First Budget", "Created your first budget", "🎯", "budget", 50),
    [BudgetMaster] = ("Budget Master", "Stayed under budget for 3 consecutive months", "👑", "budget", 500),
    [CategoryChampion] = ("Category Champion", "All categories under budget this period", "🏆", "budget", 100),
    
    // Savings achievements
    [FirstSavingsGoal] = ("First Steps", "Created your first savings goal", "🌱", "savings", 50),
    [GoalAchiever] = ("Goal Achiever", "Reached a savings goal", "⭐", "savings", 200),
    [Overachiever] = ("Overachiever", "Blew past a savings goal — saved 25% beyond target!", "🚀", "savings", 250),
    [HundredClub] = ("$100 Club", "Saved your first $100!", "💯", "savings", 75),
    [EmergencyFundStarter] = ("Safety Net", "Saved $500 - your emergency fund is growing!", "🛡️", "savings", 150),
    [HalfwayThere] = ("Halfway There", "Reached 50% of a savings goal", "🎉", "savings", 100),
    [SavingsMilestone] = ("Thousand Dollar Saver", "Total savings reached $1,000!", "🏅", "savings", 300),
    [SavingsMilestone5k] = ("High Roller", "Total savings reached $5,000!", "💎", "savings", 600),

    // Count-Up Budget achievements
    [CountUpChampion] = ("Target Hitter", "Reached a count-up budget target", "🎯", "count_up", 75),
    [SelfCareHero] = ("Self-Care Hero", "Consistently invested in self-care", "💆", "count_up", 100),
    [GymRegular] = ("Gym Regular", "Hit fitness budget target 4+ times this month", "💪", "count_up", 100),
    [BalancedBudgeter] = ("Balanced Budgeter", "All count-up budgets within 90-110% of target", "⚖️", "count_up", 150),
    
    // Streak achievements
    [WeekStreak] = ("Week Warrior", "7 day under-budget streak", "🔥", "streak", 75),
    [MonthStreak] = ("Month Master", "30 day under-budget streak", "🌟", "streak", 300),
    [CenturyClub] = ("Century Club", "100 day streak - legendary!", "💎", "streak", 1000),
    
    // Engagement achievements
    [QuizComplete] = ("Self-Aware", "Completed the financial personality quiz", "🧠", "engagement", 25),
    [AllAccountsLinked] = ("Fully Connected", "Linked all your financial accounts", "🔗", "engagement", 100),
    [Connector] = ("Connector", "A friend signed up with your invite code", "📨", "engagement", 50),
    [Recruiter] = ("Recruiter", "A friend you invited connected an account", "🤝", "engagement", 150),

    // Cautionary ("oops") achievements — no points, shown separately from wins
    [TimeTraveler] = ("Time Traveler", "Overspent a category so hard the carryover ate into next month's budget", "⏩", "oops", 0),
    [SelectiveAmnesia] = ("Selective Amnesia", "Reset negative rollover debt — sometimes it's better to just forget!", "🧠", "oops", 0),
    [ChronicAmnesia] = ("Chronic Amnesia", "Reset rollover debt ten times. At this point it isn't forgetting, it's a lifestyle.", "🌀", "oops", 0),
    [TimeLord] = ("Time Lord", "Overspent into next month ten separate times. You don't visit the future anymore, you live there.", "⏳", "oops", 0)
  };
}

/// <summary>
/// User level definitions based on total points
/// </summary>
public static class LevelDefinitions
{
  public static readonly (string Name, int MinPoints)[] Levels =
  {
    ("Beginner", 0),
    ("Saver", 100),
    ("Budgeter", 300),
    ("Expert", 600),
    ("Master", 1000),
    ("Champion", 2000),
    ("Legend", 5000)
  };
  
  public static (string Level, int PointsToNext) GetLevel(int totalPoints)
  {
    for (int i = Levels.Length - 1; i >= 0; i--)
    {
      if (totalPoints >= Levels[i].MinPoints)
      {
        var nextLevelPoints = i < Levels.Length - 1 ? Levels[i + 1].MinPoints : int.MaxValue;
        return (Levels[i].Name, nextLevelPoints - totalPoints);
      }
    }
    return (Levels[0].Name, Levels[1].MinPoints - totalPoints);
  }

  /// <summary>
  /// Fraction (0.0–1.0) through the CURRENT level toward the next one. Returns 1.0 at the
  /// top level. Gives the client a real progress bar instead of a hardcoded placeholder.
  /// </summary>
  public static double GetProgress(int totalPoints)
  {
    for (int i = Levels.Length - 1; i >= 0; i--)
    {
      if (totalPoints >= Levels[i].MinPoints)
      {
        if (i >= Levels.Length - 1) return 1.0; // top level
        var currentMin = Levels[i].MinPoints;
        var nextMin = Levels[i + 1].MinPoints;
        var span = nextMin - currentMin;
        if (span <= 0) return 1.0;
        var progress = (double)(totalPoints - currentMin) / span;
        return progress < 0 ? 0.0 : (progress > 1.0 ? 1.0 : progress);
      }
    }
    return 0.0;
  }
}

/// <summary>
/// One cautionary ("oops") badge with its full repeat history, INCLUDING rows whose
/// condition has since been fixed (revoked) — the repeat count is the behavioral
/// signal, not just the current state.
/// </summary>
public class OopsBadgeHistoryDto
{
  public string Key { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
  public int TimesEarned { get; set; }
  public DateTime? LastEarnedAt { get; set; }
  public bool CurrentlyHeld { get; set; }
}

/// <summary>
/// Turns repeat-earned cautionary badges into plain-language behavioral signals for the
/// AI advisor. The premise: a user may BELIEVE their budget is fine while their badge
/// history says otherwise — repeatedly earning Selective Amnesia means they keep
/// resetting rollover debt, which is evidence the allocations are chronically too low.
/// </summary>
public static class OopsBadgeSignals
{
  public static List<string> Build(
      IReadOnlyList<(string Key, int TimesEarned, DateTime? LastEarnedAt, bool CurrentlyHeld)> oops)
  {
    var signals = new List<string>();
    foreach (var b in oops)
    {
      var last = b.LastEarnedAt?.ToString("yyyy-MM-dd") ?? "unknown";
      switch (b.Key)
      {
        case AchievementDefinitions.SelectiveAmnesia when b.TimesEarned >= 3:
          signals.Add(
            $"User has reset negative rollover debt {b.TimesEarned} times (last {last}). " +
            "Repeated resets are strong evidence that some category allocations are chronically too low, " +
            "even if the user believes the budget is sound. Recommend reviewing which categories keep " +
            "going negative and reworking their allocations.");
          break;
        case AchievementDefinitions.SelectiveAmnesia when b.TimesEarned == 2:
          signals.Add(
            $"User has reset negative rollover debt twice (last {last}); worth watching whether " +
            "specific categories are underfunded.");
          break;
        case AchievementDefinitions.TimeTraveler:
          var held = b.CurrentlyHeld ? " and is currently in that state" : string.Empty;
          signals.Add(
            $"User has overspent a category into next month's allowance {b.TimesEarned} time(s){held} " +
            $"(last {last}). If recurring, that category's budget is too small for real behavior.");
          break;
        case AchievementDefinitions.ChronicAmnesia:
          signals.Add(
            "Chronic Amnesia meta-badge unlocked (10+ rollover-debt resets): treat budget-rework " +
            "advice as high priority.");
          break;
        case AchievementDefinitions.TimeLord:
          signals.Add(
            "Time Lord meta-badge unlocked (10+ overspends into future months): persistent " +
            "structural overspend pattern.");
          break;
      }
    }
    return signals;
  }
}

