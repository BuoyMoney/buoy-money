using System.Text;
using BuoyAI.Infrastructure.Services.Ai;
using BuoyAI.Core.DTOs.Gamification;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Analysis;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Configuration;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for AI-powered insight generation.
/// Handles contextual insight generation, parsing, and fallbacks.
/// Extracted from AIService for better separation of concerns.
/// </summary>
public class AIInsightGenerationService : IAIInsightGenerationService
{
    private readonly IBudgetService _budgetService;
    private readonly ITransactionService _transactionService;
    private readonly Lazy<ISavingsService> _savingsService;
    private readonly IAIUsageTracker _usageTracker;
    private readonly IAIRefreshTracker _refreshTracker;
    private readonly IEntitlementService _entitlements;
    private readonly IAIInsightRepository _aiInsightRepository;
    private readonly IOnboardingQuizService _onboardingQuizService;
    private readonly ILogger<AIInsightGenerationService> _logger;
    private readonly AIModelConfiguration _modelConfiguration;
    private readonly IGeminiApiClient _geminiApiClient;
    private readonly IAiInsightQueue _insightQueue;
    private readonly BuoyAI.Infrastructure.MCP.IMcpToolExecutor _toolExecutor;
    private readonly IGeminiAgenticRunner _agenticRunner;
    private readonly Lazy<IGamificationService> _gamificationService;
    private readonly string _baseUrl;

    // Bound on how many read-only tool round-trips the insight generator may take to
    // investigate flagged anomalies before it must produce its JSON. Kept small: insight
    // generation runs off-thread and we want it cheap and bounded, not open-ended.
    private const int InsightDrillDownMaxIterations = 4;

    private const string InsightAgentSystemPrompt =
        "You are a financial analyst generating insight cards for a personal-finance app. " +
        "You are given a pre-computed financial briefing. You MAY call the provided read-only " +
        "tools to investigate before writing — in particular, when the DATA RELIABILITY section " +
        "flags a large unreviewed transaction, you may look up the underlying transactions to " +
        "judge whether a number is trustworthy. Only investigate what you need; do not call tools " +
        "gratuitously. When a figure depends on an unreviewed/flagged transaction, hedge and " +
        "suggest the user review it rather than stating it as fact, and never assume on your own " +
        "that a transaction is a transfer. When finished, respond with ONLY the JSON array of " +
        "insights in the exact format requested — no prose, no markdown fences.";

    public AIInsightGenerationService(
        IBudgetService budgetService,
        ITransactionService transactionService,
        Lazy<ISavingsService> savingsService,
        IAIUsageTracker usageTracker,
        IAIRefreshTracker refreshTracker,
        IEntitlementService entitlements,
        IAIInsightRepository aiInsightRepository,
        IOnboardingQuizService onboardingQuizService,
        ILogger<AIInsightGenerationService> logger,
        AIModelConfiguration modelConfiguration,
        IGeminiApiClient geminiApiClient,
        IAiInsightQueue insightQueue,
        BuoyAI.Infrastructure.MCP.IMcpToolExecutor toolExecutor,
        IGeminiAgenticRunner agenticRunner,
        Lazy<IGamificationService> gamificationService,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        _budgetService = budgetService;
        _transactionService = transactionService;
        _savingsService = savingsService;
        _usageTracker = usageTracker;
        _refreshTracker = refreshTracker;
        _entitlements = entitlements;
        _aiInsightRepository = aiInsightRepository;
        _onboardingQuizService = onboardingQuizService;
        _logger = logger;
        _modelConfiguration = modelConfiguration;
        _geminiApiClient = geminiApiClient;
        _insightQueue = insightQueue;
        _toolExecutor = toolExecutor;
        _agenticRunner = agenticRunner;
        _gamificationService = gamificationService;
        _baseUrl = configuration["App:BaseUrl"] ?? "https://buoy-ai.com";
    }

    public async Task<List<AIInsightDto>> GetCachedOrEnqueueInsightsAsync(int userId, AIInsightRequest request)
    {
        // Fast path: serve the last 24 hours of cached insights with no LLM call.
        var since = DateTime.UtcNow.AddHours(-24);
        var existingInsights = await _aiInsightRepository.GetRecentInsightsForUserAsync(userId, request.Context, since, false);
        if (existingInsights.Any())
        {
            return ToDto(existingInsights).Take(request.MaxInsights).ToList();
        }

        // Cache miss: generate off the request thread so a slow Gemini call never blocks
        // the HTTP response (this path's P95 was tripping the High API Latency alert). The
        // insight becomes available on a subsequent fetch once the background worker finishes.
        _insightQueue.Enqueue(userId, request.Context, request.MaxInsights);
        return new List<AIInsightDto>();
    }

    public async Task<List<AIInsightDto>> GenerateContextualInsightsAsync(int userId, AIInsightRequest request)
    {
        try
        {
            if (!_geminiApiClient.IsConfigured)
            {
                _logger.LogDebug("AI insights skipped for user {UserId}: no Gemini API key configured", userId);
                return new List<AIInsightDto>();
            }

            var userTier = await _entitlements.GetTierOrStandardAsync(userId);
            
            // Check rate limits
            if (!await _usageTracker.CanMakeRequestAsync(userId, userTier))
            {
                _logger.LogWarning("User {UserId} rate limited for AI insights", userId);
                return new List<AIInsightDto>();
            }

            // Check if we have recent insights (from last 24 hours)
            var since = DateTime.UtcNow.AddHours(-24);
            var existingInsights = await _aiInsightRepository.GetRecentInsightsForUserAsync(userId, request.Context, since, false);
            
            if (existingInsights.Any())
            {
                _logger.LogInformation("Returning existing insights for user {UserId}, context {Context}, found {Count} insights", 
                    userId, request.Context, existingInsights.Count);
                return ToDto(existingInsights).Take(request.MaxInsights).ToList();
            }

            // Generate fresh insights
            var insights = new List<AIInsightDto>();

            // Generate context-specific insights
            var contextLower = request.Context.ToLowerInvariant();
            
            if (contextLower.Contains("dashboard"))
            {
                insights.AddRange(await GenerateDashboardInsightsAsync(userId, userTier));
            }
            else if (contextLower.Contains("budget"))
            {
                insights.AddRange(await GenerateBudgetInsightsAsync(userId, userTier));
            }
            else if (contextLower.Contains("savings"))
            {
                insights.AddRange(await GenerateSavingsInsightsAsync(userId, userTier));
            }
            // Other contexts have no generator: nothing to show (never canned text).

            // Save insights to database
            foreach (var insight in insights.Take(request.MaxInsights))
            {
                var entity = ToEntity(insight, userId, request.Context);
                await _aiInsightRepository.CreateInsightAsync(entity);
            }
            
            _logger.LogInformation("Generated and saved {InsightCount} insights for user {UserId}, context {Context}", insights.Count, userId, request.Context);
            
            return insights.Take(request.MaxInsights).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating contextual insights for user {UserId}", userId);
            return new List<AIInsightDto>();
        }
    }

    public async Task<AIInsightDto> GenerateSpecificInsightAsync(int userId, AIInsightRequest request)
    {
        try
        {
            var insights = await GenerateContextualInsightsAsync(userId, request);
            return insights.FirstOrDefault() ?? CreateErrorInsightDto("no-insights", "No Insights", "No insights available at this time.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating specific insight for user {UserId}", userId);
            return CreateErrorInsightDto("error", "Error", "Unable to generate insight.");
        }
    }

    public async Task<List<AIInsightDto>> RefreshContextualInsightsAsync(int userId, string context, int maxInsights = 5)
    {
        try
        {
            if (!_geminiApiClient.IsConfigured)
            {
                // AI is off: keep what is stored and spend nothing (no archive, no refresh count).
                var storedInsights = await _aiInsightRepository.GetInsightsForUserAsync(userId, context, maxInsights);
                return ToDto(storedInsights);
            }

            var userTier = await _entitlements.GetTierOrStandardAsync(userId);
            
            _logger.LogInformation("Attempting to refresh insights for user {UserId}, context {Context}", userId, context);
            
            // Check if user can refresh (daily limit)
            if (!await _refreshTracker.CanRefreshAsync(userId, context, userTier))
            {
                var refreshStats = await _refreshTracker.GetRefreshStatsAsync(userId, context, userTier);
                var usedToday = refreshStats.UsedToday;
                var dailyLimit = refreshStats.DailyLimit;
                _logger.LogWarning("User {UserId} has exceeded daily refresh limit for context {Context}: {UsedToday}/{DailyLimit}", 
                    userId, context, usedToday, dailyLimit);
                    
                // Return existing insights instead of empty list
                var existingInsights = await _aiInsightRepository.GetInsightsForUserAsync(userId, context, maxInsights);
                return ToDto(existingInsights);
            }
            
            // Check rate limits for AI generation before starting any work
            if (!await _usageTracker.CanMakeRequestAsync(userId, userTier))
            {
                _logger.LogWarning("User {UserId} rate limited for AI generation during refresh", userId);
                var limitedInsights = await _aiInsightRepository.GetInsightsForUserAsync(userId, context, maxInsights);
                return ToDto(limitedInsights);
            }

            // Archive current active insights for this context
            await _aiInsightRepository.ArchiveInsightsAsync(userId, context);
            _logger.LogInformation("Archived existing insights for user {UserId}, context {Context}", userId, context);
            
            // Record the refresh
            await _refreshTracker.RecordRefreshAsync(userId, context);
            
            // Generate fresh insights synchronously
            var insights = new List<AIInsightDto>();
            var contextLower = context.ToLowerInvariant();
            
            _logger.LogInformation("Starting synchronous AI generation for user {UserId}, context {Context}", userId, context);
            
            if (contextLower.Contains("dashboard"))
            {
                insights.AddRange(await GenerateDashboardInsightsAsync(userId, userTier));
            }
            else if (contextLower.Contains("budget"))
            {
                insights.AddRange(await GenerateBudgetInsightsAsync(userId, userTier));
            }
            else if (contextLower.Contains("savings"))
            {
                insights.AddRange(await GenerateSavingsInsightsAsync(userId, userTier));
            }
            // Other contexts have no generator: nothing to show (never canned text).

            // Save insights to database
            foreach (var insight in insights.Take(maxInsights))
            {
                var entity = ToEntity(insight, userId, context);
                await _aiInsightRepository.CreateInsightAsync(entity);
            }
            
            _logger.LogInformation("Synchronous AI generation completed successfully for user {UserId}, context {Context} with {InsightCount} insights", 
                userId, context, insights.Count);
            
            return insights.Take(maxInsights).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing insights for user {UserId}, context {Context}", userId, context);
            
            // Return existing insights on error
            var existingInsights = await _aiInsightRepository.GetInsightsForUserAsync(userId, context, maxInsights);
            return ToDto(existingInsights);
        }
    }

    public async Task<bool> DismissInsightAsync(int userId, string insightId)
    {
        try
        {
            return await _aiInsightRepository.DismissInsightAsync(userId, insightId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dismissing insight {InsightId} for user {UserId}", insightId, userId);
            return false;
        }
    }

    public async Task<string> BuildFinancialContextAsync(int userId, string context)
    {
        var contextBuilder = new StringBuilder();
        
        try
        {
            // Add current date context for AI understanding
            var now = DateTime.UtcNow;
            contextBuilder.AppendLine("=== TIME CONTEXT ===");
            contextBuilder.AppendLine($"Today's Date: {now:MMMM d, yyyy} ({now:dddd})");
            contextBuilder.AppendLine($"Current Month: {now:MMMM yyyy}");
            contextBuilder.AppendLine($"Previous Month: {now.AddMonths(-1):MMMM yyyy}");
            contextBuilder.AppendLine("IMPORTANT: All financial data below is from the CURRENT time period. When making recommendations, focus on current/upcoming actions, not past months.");
            contextBuilder.AppendLine();
            
            // Get recent transactions. Exclude transfers and credit-card payments
            // (internal money movement, not real income/spend) — including them skews
            // the AI's view of cash flow and produces misleading insights. Mirrors the
            // filtering already done in AICategorizationService.
            var creditPaymentId = (int)TransactionCategory.CreditPayment;
            var transferId = (int)TransactionCategory.Transfer;
            var transactionFilter = new TransactionFilterRequest
            {
                StartDate = now.AddDays(-30),
                EndDate = now,
                // Pull the window (not just the ~10 we display) so the DATA RELIABILITY
                // assessment below sees the same transactions that make up the totals.
                PageSize = 100
            };

            var transactionResponse = await _transactionService.GetTransactionsWithPaginationAsync(userId, transactionFilter);
            var windowTransactions = transactionResponse?.Transactions ?? new List<TransactionDto>();

            var recentTransactions = windowTransactions
                .Where(t => t.CategoryId != creditPaymentId && t.CategoryId != transferId)
                .Take(10)
                .ToList();

            if (recentTransactions?.Any() == true)
            {
                contextBuilder.AppendLine($"=== RECENT TRANSACTIONS ({now.AddDays(-30):MMM d} to {now:MMM d, yyyy}) ===");
                foreach (var transaction in recentTransactions)
                {
                    var type = transaction.Type == TransactionType.Credit ? "Income" : "Expense";
                    contextBuilder.AppendLine($"- {transaction.Date:MMM dd}: {type} ${Math.Abs(transaction.Amount):F2} at {transaction.MerchantName} ({transaction.CategoryName})");
                }
                contextBuilder.AppendLine();
            }
            
            // Get active budget
            var activeBudget = await _budgetService.GetActiveBudgetAsync(userId);
            if (activeBudget != null)
            {
                contextBuilder.AppendLine("=== CURRENT BUDGET PERFORMANCE ===");
                contextBuilder.AppendLine($"Budget: {activeBudget.Name}");
                contextBuilder.AppendLine($"Period: {activeBudget.CurrentPeriodStart:MMM dd} - {activeBudget.CurrentPeriodEnd:MMM dd}");
                contextBuilder.AppendLine($"Days Elapsed: {activeBudget.DaysElapsed} / {activeBudget.DaysInCurrentPeriod}");
                contextBuilder.AppendLine($"Total Income: ${activeBudget.TotalIncome:F2}");
                contextBuilder.AppendLine($"Total Expenses: ${activeBudget.TotalExpenses:F2}");
                
                if (activeBudget.Categories?.Any() == true)
                {
                    contextBuilder.AppendLine("Budget Categories:");
                    foreach (var category in activeBudget.Categories.Take(5))
                    {
                        var utilizationPercent = category.AllocatedAmount > 0 ? (category.SpentAmount / category.AllocatedAmount * 100) : 0;
                        var status = utilizationPercent > 100 ? "OVER BUDGET" : utilizationPercent > 80 ? "WARNING" : "ON TRACK";
                        contextBuilder.AppendLine($"- {category.CategoryName}: ${category.SpentAmount:F2} / ${category.AllocatedAmount:F2} ({utilizationPercent:F1}% used) - {status}");
                    }
                }
                contextBuilder.AppendLine();
            }
            
            // Get spending summary
            var spendingSummary = await _transactionService.GetTransactionSummaryAsync(userId, 
                now.AddDays(-30), now);
                
            if (spendingSummary != null)
            {
                contextBuilder.AppendLine($"=== SPENDING SUMMARY ({now.AddDays(-30):MMM d} to {now:MMM d, yyyy}) ===");
                contextBuilder.AppendLine($"Total Income: ${spendingSummary.TotalIncome:F2}");
                contextBuilder.AppendLine($"Total Expenses: ${Math.Abs(spendingSummary.TotalExpenses):F2}");
                contextBuilder.AppendLine($"Net Amount: ${spendingSummary.NetAmount:F2}");
                
                if (spendingSummary.CategoryTotals?.Any() == true)
                {
                    contextBuilder.AppendLine("Top Spending Categories:");
                    foreach (var category in spendingSummary.CategoryTotals.OrderByDescending(c => Math.Abs(c.Value)).Take(5))
                    {
                        contextBuilder.AppendLine($"- {category.Key}: ${Math.Abs(category.Value):F2}");
                    }
                }
                contextBuilder.AppendLine();

                // How much of the spend above has the user actually reviewed? Unreviewed rows
                // can be miscategorized or not real spend (an untagged transfer) — surface that
                // so the model hedges on figures driven by material, unconfirmed transactions
                // instead of asserting conclusions on data the user hasn't seen yet.
                var reliability = InsightReliabilityAnalyzer.Assess(
                    windowTransactions, Math.Abs(spendingSummary.TotalExpenses));
                contextBuilder.AppendLine(InsightReliabilityAnalyzer.FormatForPrompt(reliability));
            }
            
            // Get category statistics for additional insights
            var categoryStats = await _transactionService.GetCategoryStatisticsAsync(userId, 
                now.AddDays(-30), now);
                
            if (categoryStats?.Any() == true)
            {
                contextBuilder.AppendLine("=== SPENDING PATTERNS ===");
                foreach (var stat in categoryStats.Take(3))
                {
                    contextBuilder.AppendLine($"- {stat.CategoryName}: {stat.TransactionCount} transactions, avg ${stat.AverageAmount:F2} ({stat.Percentage:F1}% of total spending)");
                }
                contextBuilder.AppendLine();
            }
            
            // Get savings goals and progress
            var savingsGoals = await _savingsService.Value.GetSavingsGoalsAsync(userId);
            var activeGoals = savingsGoals?.Where(g => g.IsActive && !g.IsCompleted).ToList() ?? new List<SavingsGoalDto>();
            if (activeGoals.Any())
            {
                contextBuilder.AppendLine("=== SAVINGS GOALS & PROGRESS ===");
                foreach (var goal in activeGoals.Take(5))
                {
                    var progressPercent = goal.TargetAmount > 0 ? (goal.CurrentAmount / goal.TargetAmount * 100) : 0;
                    var remainingAmount = goal.TargetAmount - goal.CurrentAmount;
                    var status = progressPercent >= 100 ? "COMPLETED" : progressPercent >= 75 ? "ALMOST THERE" : progressPercent >= 25 ? "PROGRESSING" : "JUST STARTED";
                    
                    contextBuilder.AppendLine($"- {goal.Name}: ${goal.CurrentAmount:F2} / ${goal.TargetAmount:F2} ({progressPercent:F1}% complete) - {status}");
                    
                    if (goal.TargetDate.HasValue)
                    {
                        var daysRemaining = (goal.TargetDate.Value - now).Days;
                        if (daysRemaining > 0)
                        {
                            var monthlyNeeded = remainingAmount > 0 ? (remainingAmount / (decimal)(daysRemaining / 30.0)) : 0;
                            contextBuilder.AppendLine($"  Target: {goal.TargetDate.Value:MMM dd, yyyy} ({daysRemaining} days) - Need ${monthlyNeeded:F2}/month");
                        }
                    }
                }
                contextBuilder.AppendLine();
            }

            await AppendGamificationSignalsAsync(userId, contextBuilder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build complete financial context for user {UserId}", userId);
            contextBuilder.AppendLine("=== FINANCIAL CONTEXT ===");
            contextBuilder.AppendLine("Financial data temporarily unavailable.");
        }
        
        return contextBuilder.ToString();
    }

    /// <summary>
    /// Badge-history behavioral signals (repeat-earned oops badges). Revealed behavior can
    /// contradict a user's stated belief that their budget is fine: repeated Selective
    /// Amnesia earns mean rollover debt keeps getting reset, so allocations likely need
    /// rework. Best-effort; never breaks context building.
    /// </summary>
    private async Task AppendGamificationSignalsAsync(int userId, StringBuilder contextBuilder)
    {
        try
        {
            var oops = await _gamificationService.Value.GetOopsBadgeHistoryAsync(userId);
            if (oops.Count == 0)
            {
                return;
            }

            var signals = OopsBadgeSignals.Build(
                oops.Select(o => (o.Key, o.TimesEarned, o.LastEarnedAt, o.CurrentlyHeld)).ToList());

            contextBuilder.AppendLine("=== BEHAVIORAL SIGNALS (BADGE HISTORY) ===");
            foreach (var o in oops)
            {
                var held = o.CurrentlyHeld ? ", currently active" : string.Empty;
                contextBuilder.AppendLine($"- {o.Title}: earned {o.TimesEarned}x, last {o.LastEarnedAt:yyyy-MM-dd}{held}");
            }
            foreach (var signal in signals)
            {
                contextBuilder.AppendLine($"SIGNAL: {signal}");
            }
            contextBuilder.AppendLine("These are revealed-behavior signals and may contradict the user's stated beliefs about their budget; weigh them when generating insights.");
            contextBuilder.AppendLine();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append gamification signals for user {UserId}", userId);
        }
    }

    public string BuildUserProfileContext(UserProfileForAIDto? userProfile)
    {
        if (userProfile == null)
        {
            return "User Profile: No profile data available. Provide general financial advice.";
        }

        var context = new StringBuilder();
        context.AppendLine("User Profile:");
        
        if (userProfile.PrimaryFinancialGoals.Any())
        {
            context.AppendLine($"- Primary Goals: {string.Join(", ", userProfile.PrimaryFinancialGoals)}");
        }
        
        if (userProfile.BiggestChallenges.Any())
        {
            context.AppendLine($"- Biggest Challenges: {string.Join(", ", userProfile.BiggestChallenges)}");
        }
        
        if (userProfile.SpendingPersonalities.Any())
        {
            context.AppendLine($"- Spending Personality: {string.Join(", ", userProfile.SpendingPersonalities)}");
        }
        
        if (!string.IsNullOrEmpty(userProfile.SubscriptionAwareness))
        {
            context.AppendLine($"- Subscription Awareness: {userProfile.SubscriptionAwareness}");
        }
        
        if (userProfile.SavingsBarriers.Any())
        {
            context.AppendLine($"- Savings Barriers: {string.Join(", ", userProfile.SavingsBarriers)}");
        }
        
        if (userProfile.PreferredInsightTypes.Any())
        {
            context.AppendLine($"- Preferred Insights: {string.Join(", ", userProfile.PreferredInsightTypes)}");
        }
        
        if (!string.IsNullOrEmpty(userProfile.FinancialExperience))
        {
            context.AppendLine($"- Financial Experience: {userProfile.FinancialExperience}");
        }
        
        context.AppendLine($"- Has Used Budgeting Apps: {(userProfile.HasUsedBudgetingApps ? "Yes" : "No")}");
        
        if (!string.IsNullOrEmpty(userProfile.AdditionalNotes))
        {
            context.AppendLine($"- Additional Notes: {userProfile.AdditionalNotes}");
        }

        return context.ToString();
    }

    #region Private Methods

    /// <summary>
    /// Runs insight generation as a bounded, read-only agentic call: the model may call the
    /// read-only MCP tools to investigate flagged anomalies before producing its JSON, then we
    /// parse it. Falls back to plain text generation when the agentic path is unavailable (no
    /// API key) or yields nothing, so behavior degrades gracefully.
    /// </summary>
    private async Task<List<AIInsightDto>> GenerateInsightsWithAgentAsync(int userId, string model, string prompt, string context)
    {
        // Both the drill-down turns and the text fallback below are this user's spend.
        using var usageScope = AiUsageScope.ForUser(userId);
        try
        {
            var readOnlyTools = _toolExecutor.GetGeminiFunctionDeclarations(readOnlyOnly: true);
            var result = await _agenticRunner.RunAsync(
                model,
                InsightAgentSystemPrompt,
                prompt,
                readOnlyTools,
                (name, args) => _toolExecutor.ExecuteToolAsync(userId, name, args),
                InsightDrillDownMaxIterations);

            if (result.ToolCalls.Count > 0)
            {
                _logger.LogInformation(
                    "Insight drill-down for user {UserId} ({Context}) made {Count} read-only tool call(s): {Tools}",
                    userId, context, result.ToolCalls.Count,
                    string.Join(", ", result.ToolCalls.Select(c => c.ToolName)));
            }

            if (!string.IsNullOrWhiteSpace(result.FinalText))
            {
                return ParseInsightsFromResponse(result.FinalText, context);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Agentic insight generation failed for user {UserId} ({Context}); falling back to text generation",
                userId, context);
        }

        var response = await _geminiApiClient.GenerateContentAsync(prompt, model);
        return ParseInsightsFromResponse(response, context);
    }

    private async Task<List<AIInsightDto>> GenerateDashboardInsightsAsync(int userId, SubscriptionTier tier)
    {
        try
        {
            var models = _modelConfiguration.GetModelsForTier(tier);
            var model = models[0]; // Primary model for tier
            
            // Get user profile from quiz to personalize insights
            var userProfile = await _onboardingQuizService.GetUserProfileForAIAsync(userId);
            var profileContext = BuildUserProfileContext(userProfile);
            
            // Get actual financial data for context
            var financialContext = await BuildFinancialContextAsync(userId, "dashboard");
            
            // Get application context for actionable recommendations
            var applicationContext = BuildApplicationContext("dashboard");
            
            var prompt = $@"You are a financial AI assistant providing dashboard insights. Generate 2-3 specific, actionable insights for a user's financial dashboard.

{applicationContext}

{profileContext}

{financialContext}

Based on this user profile AND their actual financial data, provide insights that align with their goals, challenges, and current financial situation. Include specific routes/pages in your actionSuggestion field when recommending actions.

Return ONLY a JSON array of insights in this exact format:
[
  {{
    ""type"": ""spending|saving|recommendation|alert"",
    ""title"": ""Brief insight title"",
    ""message"": ""Detailed insight message"",
    ""actionSuggestion"": ""Specific actionable recommendation"",
    ""confidence"": 0.85,
    ""priority"": ""high|medium|low"",
    ""suggestedAction"": {{ ""type"": ""navigate"", ""label"": ""Review these transactions"", ""route"": ""/transactions"" }}
  }}
]

The ""suggestedAction"" field is OPTIONAL — include it only when there is a concrete next step, and omit it otherwise. Two forms:
  - Navigate: {{ ""type"": ""navigate"", ""label"": ""<button text>"", ""route"": ""/transactions|/budgets|/savings|/subscriptions|/accounts"" }} — sends the user to a page. Use this to prompt the user to review unreviewed/flagged transactions.
  - Agent handoff: {{ ""type"": ""agent_handoff"", ""label"": ""<button text>"", ""instruction"": ""<precise task for a follow-up assistant that can make the change on the user's behalf>"", ""summary"": ""<one line describing what pressing the button will do>"" }} — only for a concrete change the app can perform. NEVER propose reclassifying a transaction as a transfer yourself; if a transfer is suspected, use a navigate action asking the user to review it instead.

Focus on their specific financial goals, spending personality, and preferred insight types.";
            
            var insights = await GenerateInsightsWithAgentAsync(userId, model, prompt, "dashboard");

            return insights;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating dashboard insights for user {UserId}", userId);
            return new List<AIInsightDto>(); // no canned replacement text; the caller shows nothing
        }
    }

    private async Task<List<AIInsightDto>> GenerateBudgetInsightsAsync(int userId, SubscriptionTier tier)
    {
        try
        {
            var models = _modelConfiguration.GetModelsForTier(tier);
            var model = models[0]; // Primary model for tier
            
            // Get user profile from quiz to personalize insights
            var userProfile = await _onboardingQuizService.GetUserProfileForAIAsync(userId);
            var profileContext = BuildUserProfileContext(userProfile);
            
            // Get actual financial data for context
            var financialContext = await BuildFinancialContextAsync(userId, "budget");
            
            // Get application context for actionable recommendations
            var applicationContext = BuildApplicationContext("budget");
            
            var prompt = $@"You are a financial AI assistant analyzing a user's budget performance. Generate 2-3 specific insights about budget management.

{applicationContext}

{profileContext}

{financialContext}

Based on this user profile AND their actual budget performance data, provide budget insights that address their real spending patterns and challenges. Include specific routes/pages in your actionSuggestion field when recommending actions.

Return ONLY a JSON array of insights in this exact format:
[
  {{
    ""type"": ""budget|spending|recommendation"",
    ""title"": ""Brief insight title"",
    ""message"": ""Detailed insight message about budget performance"",
    ""actionSuggestion"": ""Specific action to improve budget management"",
    ""confidence"": 0.85,
    ""priority"": ""high|medium|low"",
    ""suggestedAction"": {{ ""type"": ""navigate"", ""label"": ""Review these transactions"", ""route"": ""/transactions"" }}
  }}
]

The ""suggestedAction"" field is OPTIONAL — include it only when there is a concrete next step, and omit it otherwise. Two forms:
  - Navigate: {{ ""type"": ""navigate"", ""label"": ""<button text>"", ""route"": ""/transactions|/budgets|/savings|/subscriptions|/accounts"" }} — sends the user to a page. Use this to prompt the user to review unreviewed/flagged transactions.
  - Agent handoff: {{ ""type"": ""agent_handoff"", ""label"": ""<button text>"", ""instruction"": ""<precise task for a follow-up assistant that can make the change on the user's behalf>"", ""summary"": ""<one line describing what pressing the button will do>"" }} — only for a concrete change the app can perform. NEVER propose reclassifying a transaction as a transfer yourself; if a transfer is suspected, use a navigate action asking the user to review it instead.

Focus on budget optimization strategies tailored to their spending habits and challenges.";

            var insights = await GenerateInsightsWithAgentAsync(userId, model, prompt, "budget");

            return insights;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating budget insights for user {UserId}", userId);
            return new List<AIInsightDto>(); // no canned replacement text; the caller shows nothing
        }
    }

    private async Task<List<AIInsightDto>> GenerateSavingsInsightsAsync(int userId, SubscriptionTier tier)
    {
        try
        {
            var models = _modelConfiguration.GetModelsForTier(tier);
            var model = models[0]; // Primary model for tier
            
            // Get user profile from quiz to personalize insights
            var userProfile = await _onboardingQuizService.GetUserProfileForAIAsync(userId);
            var profileContext = BuildUserProfileContext(userProfile);
            
            // Get actual financial data for context
            var financialContext = await BuildFinancialContextAsync(userId, "savings");
            
            // Get application context for actionable recommendations
            var applicationContext = BuildApplicationContext("savings");
            
            var prompt = $@"You are a financial AI assistant analyzing savings opportunities. Generate 2-3 specific insights about savings growth.

{applicationContext}

{profileContext}

{financialContext}

Based on this user profile AND their actual financial data, provide savings insights that address their real spending patterns, budget surplus/deficit, and progress toward goals. Include specific routes/pages in your actionSuggestion field when recommending actions.

Return ONLY a JSON array of insights in this exact format:
[
  {{
    ""type"": ""savings|prediction|recommendation"",
    ""title"": ""Brief insight title"",
    ""message"": ""Detailed message about savings progress or opportunities"",
    ""actionSuggestion"": ""Specific action to improve savings"",
    ""confidence"": 0.85,
    ""priority"": ""high|medium|low"",
    ""suggestedAction"": {{ ""type"": ""navigate"", ""label"": ""Review these transactions"", ""route"": ""/transactions"" }}
  }}
]

The ""suggestedAction"" field is OPTIONAL — include it only when there is a concrete next step, and omit it otherwise. Two forms:
  - Navigate: {{ ""type"": ""navigate"", ""label"": ""<button text>"", ""route"": ""/transactions|/budgets|/savings|/subscriptions|/accounts"" }} — sends the user to a page. Use this to prompt the user to review unreviewed/flagged transactions.
  - Agent handoff: {{ ""type"": ""agent_handoff"", ""label"": ""<button text>"", ""instruction"": ""<precise task for a follow-up assistant that can make the change on the user's behalf>"", ""summary"": ""<one line describing what pressing the button will do>"" }} — only for a concrete change the app can perform. NEVER propose reclassifying a transaction as a transfer yourself; if a transfer is suspected, use a navigate action asking the user to review it instead.

Focus on savings strategies that overcome their identified barriers and support their goals.";

            var insights = await GenerateInsightsWithAgentAsync(userId, model, prompt, "savings");

            return insights;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating savings insights for user {UserId}", userId);
            return new List<AIInsightDto>(); // no canned replacement text; the caller shows nothing
        }
    }

    private string BuildApplicationContext(string context)
    {
        var contextBuilder = new StringBuilder();
        
        contextBuilder.AppendLine("=== BUOY MONEY APPLICATION CONTEXT ===");
        contextBuilder.AppendLine("You are an AI assistant for Buoy Money, an AI-powered personal finance management application.");
        contextBuilder.AppendLine($"Application URL: {_baseUrl}");
        contextBuilder.AppendLine();
        
        contextBuilder.AppendLine("AVAILABLE FEATURES & ACTIONS:");
        
        // Dashboard context
        if (context == "dashboard" || context == "general")
        {
            contextBuilder.AppendLine("• Dashboard (/dashboard) - Financial overview with AI insights, net worth, recent transactions");
        }
        
        // Accounts context
        contextBuilder.AppendLine("• Accounts (/accounts) - Connect and manage bank accounts via Plaid integration");
        contextBuilder.AppendLine("• Account Issues (/update-connection/:id) - Fix disconnected account connections");
        
        // Transactions context
        contextBuilder.AppendLine("• Transactions (/transactions) - View, filter, and categorize all transactions");
        
        // Budget context
        if (context == "budget" || context == "dashboard" || context == "general")
        {
            contextBuilder.AppendLine("• Budgets (/budgets) - Create/manage budgets, track spending vs. allocations");
            contextBuilder.AppendLine("• Count-up Budgeting - Flexible spending control for specific categories");
        }
        
        // Savings context
        if (context == "savings" || context == "dashboard" || context == "general")
        {
            contextBuilder.AppendLine("• Savings Goals (/savings) - Set target amounts/dates, track progress, allocate funds");
        }
        
        // Subscriptions context
        contextBuilder.AppendLine("• Subscriptions (/subscriptions) - Auto-detect and manage recurring payments");
        
        // Profile and security
        contextBuilder.AppendLine("• Profile (/profile) - Personal info, financial personality quiz for AI personalization");
        contextBuilder.AppendLine("• Security (/settings) - Multi-factor authentication (TOTP, Email, Passkeys)");
        
        // Notifications
        contextBuilder.AppendLine("• Notifications (/notifications) - Budget alerts, security notifications, insights");
        
        contextBuilder.AppendLine();
        contextBuilder.AppendLine("ACTIONABLE RECOMMENDATIONS GUIDELINES:");
        contextBuilder.AppendLine("- Always suggest specific pages/routes when recommending actions");
        contextBuilder.AppendLine("- Use route format like '/budgets' or '/savings' in action suggestions");
        contextBuilder.AppendLine("- Provide concrete next steps, not vague advice");
        contextBuilder.AppendLine("- Consider user's current financial personality and goals");
        contextBuilder.AppendLine("- Prioritize high-impact actions based on their financial data");
        contextBuilder.AppendLine("- Focus recommendations on the CURRENT month and future actions, NOT past months");
        contextBuilder.AppendLine("- When referencing spending data, be specific about whether it's current month, last 30 days, or which time period");
        contextBuilder.AppendLine();
        
        return contextBuilder.ToString();
    }

    /// <summary>
    /// Validates and maps the model's suggested action. Returns null for anything malformed or
    /// unusable (unknown type, navigate with no route, handoff with no instruction) so the UI
    /// simply shows no button rather than a broken one. Labels/instructions are length-capped.
    /// </summary>
    private SuggestedAction? MapSuggestedAction(SuggestedActionResponse? resp)
    {
        if (resp == null || string.IsNullOrWhiteSpace(resp.type) || string.IsNullOrWhiteSpace(resp.label))
        {
            return null;
        }

        var type = resp.type.Trim().ToLowerInvariant();

        if (type == SuggestedAction.TypeNavigate)
        {
            // Must be an in-app absolute path. Reject protocol-relative ("//host") and scheme-y
            // ("/\evil") forms so a model-emitted route can't send the app off-origin.
            if (string.IsNullOrWhiteSpace(resp.route)
                || !resp.route.StartsWith('/')
                || resp.route.StartsWith("//")
                || resp.route.StartsWith("/\\"))
            {
                return null;
            }

            return new SuggestedAction
            {
                Type = SuggestedAction.TypeNavigate,
                Label = TruncateText(resp.label, 60),
                Route = TruncateText(resp.route, 200)
            };
        }

        if (type == SuggestedAction.TypeAgentHandoff)
        {
            if (string.IsNullOrWhiteSpace(resp.instruction))
            {
                return null; // handoff must carry an instruction for the follow-up agent
            }

            return new SuggestedAction
            {
                Type = SuggestedAction.TypeAgentHandoff,
                Label = TruncateText(resp.label, 60),
                Instruction = TruncateText(resp.instruction, 500),
                Summary = string.IsNullOrWhiteSpace(resp.summary) ? null : TruncateText(resp.summary, 200)
            };
        }

        return null; // unknown action type
    }

    private List<AIInsightDto> ParseInsightsFromResponse(string response, string context)
    {
        try
        {
            // Try to extract JSON array from response
            var cleanedResponse = response.Trim();
            
            // Find JSON array bounds
            var jsonStart = cleanedResponse.IndexOf('[');
            var jsonEnd = cleanedResponse.LastIndexOf(']');
            
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonString = cleanedResponse.Substring(jsonStart, jsonEnd - jsonStart + 1);
                
                // Try to deserialize directly to our structured response class
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                };
                
                var insightResponses = JsonSerializer.Deserialize<List<AIInsightResponse>>(jsonString, options);
                
                if (insightResponses != null)
                {
                    var result = new List<AIInsightDto>();
                    foreach (var insightResp in insightResponses)
                    {
                        result.Add(new AIInsightDto
                        {
                            Id = $"{context}_insight_{Guid.NewGuid()}",
                            Type = insightResp.type ?? "recommendation",
                            Title = TruncateText(insightResp.title ?? "AI Insight", 60),
                            Message = TruncateText(insightResp.message ?? "No message available", 300),
                            ActionSuggestion = insightResp.actionSuggestion ?? "No action suggested",
                            SuggestedAction = MapSuggestedAction(insightResp.suggestedAction),
                            Confidence = insightResp.confidence,
                            GeneratedAt = DateTime.UtcNow,
                            Priority = insightResp.priority ?? "medium"
                        });
                    }
                    
                    _logger.LogInformation("Successfully parsed {Count} insights from structured JSON response", result.Count);
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse insights from structured JSON response for context {Context}: {Response}", context, response.Substring(0, Math.Min(200, response.Length)));
        }
        
        // Unusable reply: no insights. Never substitute canned text.
        _logger.LogWarning("No usable insights parsed for context {Context}", context);
        return new List<AIInsightDto>();
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
        
        if (lastSpace > maxLength * 0.8)
        {
            return truncated.Substring(0, lastSpace) + "...";
        }
        
        return truncated.Substring(0, maxLength - 3) + "...";
    }

    private static AIInsightDto CreateErrorInsightDto(string id, string title, string message)
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

    #endregion

    #region Entity/DTO Conversion

    // The structured SuggestedAction is persisted inside the free-form AIInsight.Data JSON
    // (under this reserved key) so we don't need a schema/migration for a new column.
    private const string SuggestedActionDataKey = "__suggestedAction";

    private static AIInsightDto ToDto(AIInsight entity)
    {
        var (data, suggestedAction) = DeserializeData(entity.Data);
        return new AIInsightDto
        {
            Id = entity.Id.ToString(),
            Type = entity.Type,
            Title = entity.Title,
            Message = entity.Message,
            ActionSuggestion = entity.ActionSuggestion,
            Data = data,
            SuggestedAction = suggestedAction,
            Confidence = entity.Confidence,
            GeneratedAt = entity.GeneratedAt,
            Priority = entity.Priority,
            IsDismissed = entity.IsDismissed,
            IsArchived = entity.IsArchived
        };
    }

    private static AIInsight ToEntity(AIInsightDto dto, int userId, string context)
    {
        return new AIInsight
        {
            UserId = userId,
            Context = context,
            Type = dto.Type,
            Title = dto.Title,
            Message = dto.Message,
            ActionSuggestion = dto.ActionSuggestion,
            Data = SerializeData(dto.Data, dto.SuggestedAction),
            Confidence = dto.Confidence,
            GeneratedAt = dto.GeneratedAt,
            Priority = dto.Priority,
            IsDismissed = dto.IsDismissed,
            IsArchived = dto.IsArchived,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>Serialize the insight's extra Data plus any SuggestedAction into one JSON blob.</summary>
    internal static string? SerializeData(Dictionary<string, object>? data, SuggestedAction? action)
    {
        if ((data == null || data.Count == 0) && action == null)
        {
            return null;
        }

        var combined = data != null ? new Dictionary<string, object>(data) : new Dictionary<string, object>();
        if (action != null)
        {
            combined[SuggestedActionDataKey] = action;
        }

        return JsonSerializer.Serialize(combined);
    }

    /// <summary>Inverse of <see cref="SerializeData"/>: split the JSON blob back into Data + SuggestedAction.</summary>
    internal static (Dictionary<string, object>? Data, SuggestedAction? Action) DeserializeData(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return (null, null);
        }

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(raw);
            if (dict == null)
            {
                return (null, null);
            }

            SuggestedAction? action = null;
            if (dict.TryGetValue(SuggestedActionDataKey, out var value))
            {
                dict.Remove(SuggestedActionDataKey);
                if (value is JsonElement element)
                {
                    action = element.Deserialize<SuggestedAction>();
                }
            }

            return (dict.Count > 0 ? dict : null, action);
        }
        catch
        {
            return (null, null);
        }
    }

    private static List<AIInsightDto> ToDto(List<AIInsight> entities)
    {
        return entities.Select(ToDto).ToList();
    }

    #endregion
}
