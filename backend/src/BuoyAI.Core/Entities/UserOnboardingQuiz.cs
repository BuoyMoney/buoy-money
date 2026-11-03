using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Represents a user's responses to the AI onboarding quiz
/// Used to build personalized AI profile for tailored financial insights
/// </summary>
public class UserOnboardingQuiz
{
    public int Id { get; set; }
    
    [Required]
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    
    // Financial Goals & Challenges
    public string? PrimaryFinancialGoal { get; set; } // "save_more", "budget_better", "reduce_debt", "track_spending"
    public string? BiggestChallenge { get; set; } // "sticking_to_budget", "overspending", "subscription_management", "saving_discipline"
    
    // Spending Habits
    public string? SpendingPersonality { get; set; } // "impulse_buyer", "careful_planner", "subscription_collector", "deal_hunter"
    public string? SubscriptionAwareness { get; set; } // "know_all", "know_most", "unsure", "no_idea"
    
    // Savings Barriers
    public string? SavingsBarriers { get; set; } // JSON array: ["bad_habits", "lifestyle_inflation", "lack_of_discipline", "irregular_income"]
    
    // AI Assistance Preferences
    public string? PreferredInsightType { get; set; } // "spending_alerts", "saving_tips", "budget_optimization", "goal_tracking"
    
    // Experience Level
    public string? FinancialExperience { get; set; } // "beginner", "intermediate", "advanced"
    public bool HasUsedBudgetingApps { get; set; } = false;
    
    // Additional Context
    public string? AdditionalNotes { get; set; } // Free text field for additional context
    
    // Metadata
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Version { get; set; } = 1; // Allow for quiz retakes with version tracking
}
