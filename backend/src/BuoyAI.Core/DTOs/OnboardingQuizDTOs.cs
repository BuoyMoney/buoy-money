using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.DTOs;

/// <summary>
/// DTO for submitting AI onboarding quiz responses
/// </summary>
public class OnboardingQuizSubmissionDto
{
    [Required]
    public string PrimaryFinancialGoal { get; set; } = string.Empty;
    
    [Required]
    public string BiggestChallenge { get; set; } = string.Empty;
    
    [Required]
    public string SpendingPersonality { get; set; } = string.Empty;
    
    [Required]
    public string SubscriptionAwareness { get; set; } = string.Empty;
    
    /// <summary>
    /// JSON array of selected barriers: ["bad_habits", "lifestyle_inflation", etc.]
    /// </summary>
    public string? SavingsBarriers { get; set; }
    
    [Required]
    public string PreferredInsightType { get; set; } = string.Empty;
    
    [Required]
    public string FinancialExperience { get; set; } = string.Empty;
    
    public bool HasUsedBudgetingApps { get; set; } = false;
    
    public string? AdditionalNotes { get; set; }
}

/// <summary>
/// DTO for returning quiz responses to the client
/// </summary>
public class OnboardingQuizResponseDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string? PrimaryFinancialGoal { get; set; }
    public string? BiggestChallenge { get; set; }
    public string? SpendingPersonality { get; set; }
    public string? SubscriptionAwareness { get; set; }
    public string? SavingsBarriers { get; set; }
    public string? PreferredInsightType { get; set; }
    public string? FinancialExperience { get; set; }
    public bool HasUsedBudgetingApps { get; set; }
    public string? AdditionalNotes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// DTO for AI-generated insights based on quiz responses
/// </summary>
public class PersonalizedInsightDto
{
    public string InsightType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string[] RecommendedActions { get; set; } = Array.Empty<string>();
    public string Priority { get; set; } = "medium"; // low, medium, high
}

/// <summary>
/// DTO containing user profile data for AI insights generation
/// </summary>
public class UserProfileForAIDto
{
    public int UserId { get; set; }
    public string[] PrimaryFinancialGoals { get; set; } = Array.Empty<string>();
    public string[] BiggestChallenges { get; set; } = Array.Empty<string>();
    public string[] SpendingPersonalities { get; set; } = Array.Empty<string>();
    public string SubscriptionAwareness { get; set; } = string.Empty;
    public string[] SavingsBarriers { get; set; } = Array.Empty<string>();
    public string[] PreferredInsightTypes { get; set; } = Array.Empty<string>();
    public string FinancialExperience { get; set; } = string.Empty;
    public bool HasUsedBudgetingApps { get; set; }
    public string? AdditionalNotes { get; set; }
    public DateTime QuizCompletedAt { get; set; }
    public int QuizVersion { get; set; }
}
