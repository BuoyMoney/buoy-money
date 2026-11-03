using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.MCP;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;

namespace BuoyAI.Infrastructure.Services;

// Structured response classes for Gemini API
public class AIInsightResponse
{
    public string type { get; set; } = string.Empty;
    public string title { get; set; } = string.Empty;
    public string message { get; set; } = string.Empty;
    public string actionSuggestion { get; set; } = string.Empty;
    public double confidence { get; set; }
    public string priority { get; set; } = string.Empty;
    public SuggestedActionResponse? suggestedAction { get; set; }
}

// Optional structured follow-up the model may attach to an insight (lowercase to match the
// JSON the model is asked to emit). Mapped to BuoyAI.Core.DTOs.SuggestedAction on parse.
public class SuggestedActionResponse
{
    public string type { get; set; } = string.Empty; // "navigate" | "agent_handoff"
    public string label { get; set; } = string.Empty;
    public string? route { get; set; }
    public string? instruction { get; set; }
    public string? summary { get; set; }
}

/// <summary>
/// AI Service - Split into partial classes for maintainability:
/// - AIService.cs (this file): Constructor, fields, helpers, delegation methods
/// - AIService.Chat.cs: Chat query processing (ProcessQueryAsync, streaming)
/// - AIService.Gemini.cs: Gemini API calls (function calling)
/// - AIService.Context.cs: Context building (financial, application, user profile)
/// </summary>
public partial class AIService : IAIService
{
    private readonly IBudgetService _budgetService;
    private readonly ITransactionService _transactionService;
    private readonly ISavingsService _savingsService;
    private readonly IAIUsageTracker _usageTracker;
    private readonly IAIRefreshTracker _refreshTracker;
    private readonly IEntitlementService _entitlements;
    private readonly ILogger<AIService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IAIInsightRepository _aiInsightRepository;
    private readonly IOnboardingQuizService _onboardingQuizService;
    private readonly IMcpToolExecutor _toolExecutor;
    private readonly IChatToolRouter _toolRouter;
    private readonly IChatPromptCache _promptCache;
    private readonly IUserRepository _userRepository;
    private readonly IAIInsightGenerationService _insightGenerationService;
    private readonly IAiConversationService _conversationService;
    private readonly IChatEmbeddingService _chatEmbeddingService;
    private readonly IAiMemoryService _aiMemoryService;
    private readonly IAiToolSecurityService _toolSecurityService;
    private readonly AIModelConfiguration _modelConfiguration;
    private readonly IGeminiApiClient _geminiApiClient;
    private readonly string _geminiApiKey;
    private readonly string _baseUrl;
    private const int MaxToolIterations = 10;  // Allow complex multi-tool queries

    public AIService(
        IConfiguration configuration,
        IBudgetService budgetService,
        ITransactionService transactionService,
        ISavingsService savingsService,
        IAIUsageTracker usageTracker,
        IAIRefreshTracker refreshTracker,
        IEntitlementService entitlements,
        ILogger<AIService> logger,
        IAIInsightRepository aiInsightRepository,
        IOnboardingQuizService onboardingQuizService,
        IMcpToolExecutor toolExecutor,
        IChatToolRouter toolRouter,
        IChatPromptCache promptCache,
        IUserRepository userRepository,
        IAIInsightGenerationService insightGenerationService,
        IAiConversationService conversationService,
        IChatEmbeddingService chatEmbeddingService,
        IAiMemoryService aiMemoryService,
        IAiToolSecurityService toolSecurityService,
        AIModelConfiguration modelConfiguration,
        IGeminiApiClient geminiApiClient)
    {
        _budgetService = budgetService;
        _transactionService = transactionService;
        _savingsService = savingsService;
        _usageTracker = usageTracker;
        _refreshTracker = refreshTracker;
        _entitlements = entitlements;
        _logger = logger;
        _configuration = configuration;
        _aiInsightRepository = aiInsightRepository;
        _onboardingQuizService = onboardingQuizService;
        _toolExecutor = toolExecutor;
        _toolRouter = toolRouter;
        _promptCache = promptCache;
        _userRepository = userRepository;
        _insightGenerationService = insightGenerationService;
        _conversationService = conversationService;
        _chatEmbeddingService = chatEmbeddingService;
        _aiMemoryService = aiMemoryService;
        _toolSecurityService = toolSecurityService;
        _modelConfiguration = modelConfiguration;
        _geminiApiClient = geminiApiClient;
        
        _geminiApiKey = GeminiApiKey.Resolve(configuration);
        _baseUrl = configuration["App:BaseUrl"] ?? "https://buoy-ai.com";
    }

    #region Entity/DTO Conversion Helpers
    
    private AIInsightDto ToDto(AIInsight entity)
    {
        return new AIInsightDto
        {
            Id = entity.Id.ToString(),
            Type = entity.Type,
            Title = entity.Title,
            Message = entity.Message,
            ActionSuggestion = entity.ActionSuggestion,
            Data = string.IsNullOrEmpty(entity.Data) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(entity.Data),
            Confidence = entity.Confidence,
            GeneratedAt = entity.GeneratedAt,
            Priority = entity.Priority,
            IsDismissed = entity.IsDismissed,
            IsArchived = entity.IsArchived
        };
    }

    private AIInsight ToEntity(AIInsightDto dto, int userId, string context)
    {
        return new AIInsight
        {
            UserId = userId,
            Context = context,
            Type = dto.Type,
            Title = dto.Title,
            Message = dto.Message,
            ActionSuggestion = dto.ActionSuggestion,
            Data = dto.Data != null ? JsonSerializer.Serialize(dto.Data) : null,
            Confidence = dto.Confidence,
            GeneratedAt = dto.GeneratedAt,
            Priority = dto.Priority,
            IsDismissed = dto.IsDismissed,
            IsArchived = dto.IsArchived,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private List<AIInsightDto> ToDto(List<AIInsight> entities)
    {
        return entities.Select(ToDto).ToList();
    }
    
    #endregion

    #region Insight Generation Delegation (to AIInsightGenerationService)

    public Task<List<AIInsightDto>> GenerateContextualInsightsAsync(int userId, AIInsightRequest request)
        => _insightGenerationService.GenerateContextualInsightsAsync(userId, request);

    public Task<List<AIInsightDto>> GetCachedOrEnqueueInsightsAsync(int userId, AIInsightRequest request)
        => _insightGenerationService.GetCachedOrEnqueueInsightsAsync(userId, request);

    public Task<AIInsightDto> GenerateSpecificInsightAsync(int userId, AIInsightRequest request)
        => _insightGenerationService.GenerateSpecificInsightAsync(userId, request);

    public Task<bool> DismissInsightAsync(int userId, string insightId)
        => _insightGenerationService.DismissInsightAsync(userId, insightId);

    public Task<List<AIInsightDto>> RefreshContextualInsightsAsync(int userId, string context, int maxInsights = 5)
        => _insightGenerationService.RefreshContextualInsightsAsync(userId, context, maxInsights);

    #endregion

    #region Usage Stats

    public async Task<AIUsageStats> GetUsageStatsAsync(int userId)
    {
        try
        {
            var userTier = await _entitlements.GetTierOrStandardAsync(userId);
            var monthlyUsage = await _usageTracker.GetMonthlyUsageAsync(userId);
            var limits = _entitlements.GetAiLimits(userTier);
            
            // Check monthly cost limits
            var (_, warningLevel, usagePercent) = await _usageTracker.CheckMonthlyLimitAsync(userId, userTier);
            
            // Get monthly budget for tier
            var monthlyBudget = limits.MonthlyCost;
            
            // Get refresh stats for common contexts
            var refreshStats = new Dictionary<string, RefreshStats>();
            var contexts = new[] { "dashboard", "budget", "savings" };
            
            foreach (var context in contexts)
            {
                var (usedToday, dailyLimit) = await _refreshTracker.GetRefreshStatsAsync(userId, context, userTier);
                refreshStats[context] = new RefreshStats
                {
                    UsedToday = usedToday,
                    DailyLimit = dailyLimit,
                    LastRefreshAt = null // We could add this if needed
                };
            }
            
            return new AIUsageStats
            {
                RequestsToday = monthlyUsage?.RequestsToday ?? 0,
                RequestsThisMonth = monthlyUsage?.RequestsThisMonth ?? 0,
                DailyLimit = limits.DailyRequests,
                MonthlyLimit = limits.HourlyRequests * 24 * 30, // Approximate
                CostToday = monthlyUsage?.CostToday ?? 0,
                CostThisMonth = monthlyUsage?.CostThisMonth ?? 0,
                MonthlyBudget = monthlyBudget,
                UsagePercent = usagePercent,
                WarningLevel = warningLevel,
                SubscriptionTier = userTier.ToString(),
                LastRequestAt = monthlyUsage?.LastRequestAt ?? DateTime.MinValue,
                RefreshStats = refreshStats
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting usage stats for user {UserId}", userId);
            return new AIUsageStats();
        }
    }

    #endregion

    #region Helper Methods

    private AIInsightDto CreateErrorInsightDto(string id, string title, string message)
    {
        return new AIInsightDto
        {
            Id = id,
            Type = "info",
            Title = title,
            Message = message,
            Confidence = 1.0,
            GeneratedAt = DateTime.UtcNow,
            Priority = "low"
        };
    }

    private string TruncateText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
            
        if (text.Length <= maxLength)
            return text;
            
        // Try to truncate at word boundary
        var truncated = text.Substring(0, maxLength);
        var lastSpace = truncated.LastIndexOf(' ');
        
        if (lastSpace > maxLength * 0.8) // Only use word boundary if it's not too far back
        {
            return truncated.Substring(0, lastSpace) + "...";
        }
        
        // Fallback to hard truncation
        return truncated.Substring(0, maxLength - 3) + "...";
    }

    #endregion
}
