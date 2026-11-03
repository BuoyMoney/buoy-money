using BuoyAI.Core.DTOs;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service interface for managing user onboarding quiz functionality
/// </summary>
public interface IOnboardingQuizService
{
    /// <summary>
    /// Submit a new quiz response for a user
    /// </summary>
    Task<OnboardingQuizResponseDto> SubmitQuizAsync(int userId, OnboardingQuizSubmissionDto submission);
    
    /// <summary>
    /// Get the latest quiz response for a user
    /// </summary>
    Task<OnboardingQuizResponseDto?> GetLatestQuizAsync(int userId);
    
    /// <summary>
    /// Get all quiz versions for a user (for history/tracking)
    /// </summary>
    Task<IEnumerable<OnboardingQuizResponseDto>> GetQuizHistoryAsync(int userId);
    
    /// <summary>
    /// Check if a user has completed the onboarding quiz
    /// </summary>
    Task<bool> HasCompletedQuizAsync(int userId);
    
    /// <summary>
    /// Generate personalized insights based on quiz responses
    /// </summary>
    Task<IEnumerable<PersonalizedInsightDto>> GeneratePersonalizedInsightsAsync(int userId);
    
    /// <summary>
    /// Get user profile data for AI insights generation
    /// </summary>
    Task<UserProfileForAIDto?> GetUserProfileForAIAsync(int userId);
}
