using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service interface for AI-powered insight generation.
/// Handles contextual insight generation, parsing, and fallbacks.
/// Extracted from AIService for better separation of concerns.
/// </summary>
public interface IAIInsightGenerationService
{
    /// <summary>
    /// Generate contextual insights for a specific context (dashboard, budget, savings).
    /// Returns cached insights if available, otherwise generates fresh ones.
    /// </summary>
    Task<List<AIInsightDto>> GenerateContextualInsightsAsync(int userId, AIInsightRequest request);

    /// <summary>
    /// Non-blocking read path: returns the 24h-cached insights immediately if present;
    /// on a cache miss it queues background generation and returns an empty list rather
    /// than blocking the request on a slow Gemini call. The insight appears on a later
    /// fetch once the background worker finishes. Use this from hot read endpoints
    /// (dashboard insights, savings overview) instead of <see cref="GenerateContextualInsightsAsync"/>.
    /// </summary>
    Task<List<AIInsightDto>> GetCachedOrEnqueueInsightsAsync(int userId, AIInsightRequest request);

    /// <summary>
    /// Generate a single specific insight for a context.
    /// </summary>
    Task<AIInsightDto> GenerateSpecificInsightAsync(int userId, AIInsightRequest request);
    
    /// <summary>
    /// Refresh insights for a context, archiving old ones and generating new ones.
    /// Subject to daily refresh limits based on subscription tier.
    /// </summary>
    Task<List<AIInsightDto>> RefreshContextualInsightsAsync(int userId, string context, int maxInsights = 5);
    
    /// <summary>
    /// Dismiss an insight so it won't be shown again.
    /// </summary>
    Task<bool> DismissInsightAsync(int userId, string insightId);
    
    /// <summary>
    /// Build financial context string for AI prompts.
    /// </summary>
    Task<string> BuildFinancialContextAsync(int userId, string context);
    
    /// <summary>
    /// Build user profile context from onboarding quiz answers.
    /// </summary>
    string BuildUserProfileContext(UserProfileForAIDto? userProfile);
}
