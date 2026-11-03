using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tools for reading user context data like quiz results, preferences, and chat history.
/// This allows the AI to fetch user profile information on-demand rather than 
/// always including it in the prompt.
/// </summary>
[McpServerToolType]
public sealed class UserContextReadTools
{
    private readonly IOnboardingQuizService _quizService;
    private readonly IUserRepository _userRepository;
    private readonly IChatEmbeddingService _chatEmbeddingService;
    private readonly ILogger<UserContextReadTools> _logger;

    public UserContextReadTools(
        IOnboardingQuizService quizService,
        IUserRepository userRepository,
        IChatEmbeddingService chatEmbeddingService,
        ILogger<UserContextReadTools> logger)
    {
        _quizService = quizService;
        _userRepository = userRepository;
        _chatEmbeddingService = chatEmbeddingService;
        _logger = logger;
    }

    [McpServerTool(Name = "get_user_profile", ReadOnly = true)]
    [Description("Get the user's financial profile from their onboarding quiz. Includes their financial goals, challenges, spending personality, and preferences. Use this to personalize advice based on the user's situation.")]
    public async Task<string> GetUserProfileAsync(
        [Description("User ID to get profile for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_user_profile called for user {UserId}", userId);

            var profile = await _quizService.GetUserProfileForAIAsync(userId);

            if (profile == null)
            {
                return JsonSerializer.Serialize(new
                {
                    Message = "No profile data available. The user hasn't completed the onboarding quiz yet.",
                    HasCompletedQuiz = false
                });
            }

            var result = new
            {
                HasCompletedQuiz = true,
                Profile = new
                {
                    profile.PrimaryFinancialGoals,
                    profile.BiggestChallenges,
                    profile.SpendingPersonalities,
                    profile.SubscriptionAwareness,
                    profile.SavingsBarriers,
                    profile.PreferredInsightTypes,
                    profile.FinancialExperience,
                    profile.HasUsedBudgetingApps,
                    profile.AdditionalNotes
                },
                // Add interpretive context for the AI
                Interpretation = new
                {
                    ExperienceLevel = profile.FinancialExperience switch
                    {
                        "beginner" => "User is new to budgeting - use simple explanations and avoid jargon",
                        "intermediate" => "User has some experience - can use standard financial concepts",
                        "advanced" => "User is experienced - can discuss complex financial strategies",
                        _ => "Unknown experience level - adjust explanations based on their questions"
                    },
                    KeyChallenges = profile.BiggestChallenges?.Length > 0
                        ? $"Focus areas: {string.Join(", ", profile.BiggestChallenges)}"
                        : "No specific challenges identified",
                    PreferredApproach = profile.SpendingPersonalities?.Contains("planner") == true
                        ? "User prefers structured planning"
                        : profile.SpendingPersonalities?.Contains("spontaneous") == true
                            ? "User may need help with impulse control"
                            : "Standard approach recommended"
                }
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_user_profile for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_user_app_settings", ReadOnly = true)]
    [Description("Get the user's app settings including AI interaction mode, currency, and account age. Use get_user_preferences for detailed AI interaction guidance.")]
    public async Task<string> GetUserAppSettingsAsync(
        [Description("User ID to get settings for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_user_app_settings called for user {UserId}", userId);

            var user = await _userRepository.GetByIdAsync(userId);

            if (user == null)
            {
                return JsonSerializer.Serialize(new { Error = "User not found" });
            }

            var result = new
            {
                AiInteractionMode = user.AiInteractionMode.ToString(),
                AiInteractionModeDescription = user.AiInteractionMode switch
                {
                    Core.Enums.AIInteractionMode.Quick => "User prefers quick AI interactions - uses sensible defaults, still confirms write operations",
                    Core.Enums.AIInteractionMode.Clarifying => "User prefers detailed AI interactions - asks clarifying questions, confirms all writes",
                    _ => "Default mode - confirm write operations"
                },
                Currency = user.Currency,
                SubscriptionTier = user.SubscriptionTier?.ToString() ?? "None",
                CreatedAt = user.CreatedAt,
                AccountCreatedDaysAgo = (DateTime.UtcNow - user.CreatedAt).Days
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_user_app_settings for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "search_chat_history", ReadOnly = true)]
    [Description("Search the user's past AI conversations for relevant context. Use this when the user references something they discussed before, asks 'remember when' or 'like we talked about', or when you need context about their previous questions and your recommendations. Results are semantically matched - you can search by topic, not just keywords.")]
    public async Task<string> SearchChatHistoryAsync(
        [Description("User ID to search history for")] int userId,
        [Description("Search query - describe what you're looking for (e.g., 'budget advice for groceries', 'vacation savings goal discussion', 'debt payoff strategy')")] string query,
        [Description("Maximum number of results to return (default: 5, max: 10)")] int limit = 5,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: search_chat_history called for user {UserId} with query '{Query}'", userId, query);

            if (string.IsNullOrWhiteSpace(query))
            {
                return JsonSerializer.Serialize(new { Error = "Query is required", Results = Array.Empty<object>() });
            }

            // The search embeds the query with Gemini, so it follows the same consent rule as
            // in-app chat (5.1.2(i)). The user's own assistant still gets a clear answer.
            if (!AiConsent.IsCurrent(await _userRepository.GetByIdAsync(userId)))
            {
                return JsonSerializer.Serialize(new
                {
                    Message = "Chat history search uses Buoy Money's AI features, which are off for this account. Turn them on in Buoy Money under Settings > AI Assistant.",
                    Query = query,
                    Results = Array.Empty<object>()
                });
            }

            // Clamp limit to reasonable range
            limit = Math.Clamp(limit, 1, 10);

            var results = await _chatEmbeddingService.SearchChatHistoryAsync(
                userId,
                query,
                limit,
                minScore: 0.65f, // Lower threshold for better recall
                cancellationToken);

            if (results.Count == 0)
            {
                return JsonSerializer.Serialize(new 
                { 
                    Message = "No relevant past conversations found for this query.",
                    Query = query,
                    Results = Array.Empty<object>()
                });
            }

            var formattedResults = results.Select(r => new
            {
                Role = r.Role,
                Content = r.Content,
                Date = r.CreatedAt.ToString("yyyy-MM-dd"),
                RelevanceScore = Math.Round(r.Score * 100, 1) // Convert to percentage
            });

            return JsonSerializer.Serialize(new
            {
                Message = $"Found {results.Count} relevant conversation(s) from the past.",
                Query = query,
                Results = formattedResults
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in search_chat_history for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message, Results = Array.Empty<object>() });
        }
    }
}
