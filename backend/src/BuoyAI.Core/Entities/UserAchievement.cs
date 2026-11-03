namespace BuoyAI.Core.Entities;

/// <summary>
/// Tracks user achievements for gamification features
/// </summary>
public class UserAchievement
{
  public int Id { get; set; }
  public int UserId { get; set; }
  
  /// <summary>
  /// Unique identifier for the achievement type
  /// </summary>
  public string AchievementKey { get; set; } = string.Empty;
  
  /// <summary>
  /// Display title of the achievement
  /// </summary>
  public string Title { get; set; } = string.Empty;
  
  /// <summary>
  /// Description of how the achievement was earned
  /// </summary>
  public string Description { get; set; } = string.Empty;
  
  /// <summary>
  /// Icon/emoji for the achievement badge
  /// </summary>
  public string Icon { get; set; } = string.Empty;
  
  /// <summary>
  /// Category of achievement: budget, savings, streak, milestone
  /// </summary>
  public string Category { get; set; } = string.Empty;
  
  /// <summary>
  /// Points earned for this achievement
  /// </summary>
  public int Points { get; set; }
  
  /// <summary>
  /// When the achievement was earned
  /// </summary>
  public DateTime EarnedAt { get; set; }
  
  /// <summary>
  /// Whether this achievement has been shown to the user
  /// </summary>
  public bool IsNew { get; set; } = true;

  /// <summary>
  /// How many times this achievement has been earned. Only repeatable ("oops")
  /// achievements ever exceed 1; hitting a threshold can unlock a meta-badge.
  /// </summary>
  public int TimesEarned { get; set; } = 1;

  /// <summary>Most recent earn (equals EarnedAt for single-award achievements).</summary>
  public DateTime? LastEarnedAt { get; set; }

  /// <summary>
  /// Set when a condition-based achievement's condition stopped holding. Revoked rows
  /// are hidden from every read and score but preserved so TimesEarned survives.
  /// </summary>
  public DateTime? RevokedAt { get; set; }
  
  // Navigation properties
  public User User { get; set; } = null!;
}

/// <summary>
/// Tracks user streaks for gamification
/// </summary>
public class UserStreak
{
  public int Id { get; set; }
  public int UserId { get; set; }
  
  /// <summary>
  /// Type of streak: under_budget, savings_goal, login, categorization
  /// </summary>
  public string StreakType { get; set; } = string.Empty;
  
  /// <summary>
  /// Current consecutive count
  /// </summary>
  public int CurrentCount { get; set; }
  
  /// <summary>
  /// Best ever streak count
  /// </summary>
  public int BestCount { get; set; }
  
  /// <summary>
  /// Last date the streak was incremented
  /// </summary>
  public DateTime LastIncrementDate { get; set; }
  
  /// <summary>
  /// Date the current streak started
  /// </summary>
  public DateTime StreakStartDate { get; set; }

  /// <summary>
  /// Banked streak freezes. A freeze is auto-consumed to cover a missed day so a single
  /// slip doesn't reset a long streak (loss-aversion mitigation). Earned as the streak
  /// grows; capped. See GamificationService.UpdateStreaksAsync.
  /// </summary>
  public int StreakFreezes { get; set; }

  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }

  // Navigation properties
  public User User { get; set; } = null!;
}

/// <summary>
/// Tracks active mini-challenges for gamification
/// </summary>
public class UserChallenge
{
  public int Id { get; set; }
  public int UserId { get; set; }
  
  /// <summary>
  /// Challenge type: no_spend_day, category_budget, savings_boost, etc.
  /// </summary>
  public string ChallengeType { get; set; } = string.Empty;
  
  /// <summary>
  /// Display title of the challenge
  /// </summary>
  public string Title { get; set; } = string.Empty;
  
  /// <summary>
  /// Description of the challenge
  /// </summary>
  public string Description { get; set; } = string.Empty;
  
  /// <summary>
  /// Target amount or count for the challenge
  /// </summary>
  public decimal TargetValue { get; set; }
  
  /// <summary>
  /// Current progress toward the target
  /// </summary>
  public decimal CurrentValue { get; set; }
  
  /// <summary>
  /// Points earned upon completion
  /// </summary>
  public int RewardPoints { get; set; }
  
  /// <summary>
  /// When the challenge starts
  /// </summary>
  public DateTime StartDate { get; set; }
  
  /// <summary>
  /// When the challenge ends
  /// </summary>
  public DateTime EndDate { get; set; }
  
  /// <summary>
  /// Status: active, completed, failed, expired
  /// </summary>
  public string Status { get; set; } = "active";
  
  public DateTime CreatedAt { get; set; }
  public DateTime? CompletedAt { get; set; }
  
  // Navigation properties
  public User User { get; set; } = null!;
}
