using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Stores learned facts about a user from AI conversations.
/// This enables personalized AI interactions by remembering user preferences,
/// life events, and financial goals mentioned in previous conversations.
/// 
/// PRIVACY: This feature is opt-in only. Users must explicitly enable AI Memory.
/// </summary>
public class UserAiMemory
{
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    /// <summary>
    /// Type of memory: date, holiday, preference, goal, family, financial_habit, life_event
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string MemoryType { get; set; } = string.Empty;

    /// <summary>
    /// Unique key within the type (e.g., "wife_birthday", "diwali", "house_downpayment")
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string MemoryKey { get; set; } = string.Empty;

    /// <summary>
    /// The actual value (e.g., "March 15", "celebrates", "saving $50k")
    /// </summary>
    [Required]
    public string MemoryValue { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display text (e.g., "Wife's birthday is March 15")
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string DisplayText { get; set; } = string.Empty;

    /// <summary>
    /// AI confidence level (0.0-1.0) in this memory being accurate
    /// </summary>
    public decimal Confidence { get; set; } = 1.0m;

    /// <summary>
    /// The conversation session where this was learned
    /// </summary>
    public Guid? SourceSessionId { get; set; }

    /// <summary>
    /// When this memory was first learned
    /// </summary>
    public DateTime LearnedAt { get; set; }

    /// <summary>
    /// When this memory was last used in a conversation
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Number of times this memory has been used
    /// </summary>
    public int UsageCount { get; set; } = 0;

    /// <summary>
    /// Whether this memory is active (user can disable individual memories)
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// User can edit the memory - this tracks if it was manually modified
    /// </summary>
    public bool WasManuallyEdited { get; set; } = false;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation property
    public User User { get; set; } = null!;
}

/// <summary>
/// Memory types for categorization
/// </summary>
public static class AiMemoryTypes
{
    public const string Date = "date";           // Birthdays, anniversaries
    public const string Holiday = "holiday";     // Diwali, Hanukkah, Eid, Christmas
    public const string Preference = "preference"; // "Prefers detailed breakdowns"
    public const string Goal = "goal";           // "Saving for house downpayment"
    public const string Family = "family";       // "Has 2 kids", "Married"
    public const string FinancialHabit = "financial_habit"; // "Tends to overspend on dining"
    public const string LifeEvent = "life_event"; // "Starting new job in March"
}
