using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs.Subscription;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tools for reading subscription data.
/// Provides AI access to subscription tracking, upcoming bills, and detected patterns.
/// </summary>
[McpServerToolType]
public sealed class SubscriptionReadTools
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly ISuggestedSubscriptionRepository _suggestedSubscriptionRepository;
    private readonly ILogger<SubscriptionReadTools> _logger;

    public SubscriptionReadTools(
        ISubscriptionService subscriptionService,
        ISuggestedSubscriptionRepository suggestedSubscriptionRepository,
        ILogger<SubscriptionReadTools> logger)
    {
        _subscriptionService = subscriptionService;
        _suggestedSubscriptionRepository = suggestedSubscriptionRepository;
        _logger = logger;
    }

    [McpServerTool(Name = "get_subscriptions", ReadOnly = true)]
    [Description("Get all tracked subscriptions for the user. Use this to see what recurring bills and subscriptions the user is tracking.")]
    public async Task<string> GetSubscriptionsAsync(
        [Description("User ID to get subscriptions for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_subscriptions called for user {UserId}", userId);
            
            var subscriptions = await _subscriptionService.GetSubscriptionsAsync(userId);
            var subscriptionList = subscriptions.ToList();

            if (!subscriptionList.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = "No subscriptions tracked yet. The user can add subscriptions manually or we can detect them from their transaction history.",
                    TotalSubscriptions = 0,
                    MonthlyTotal = 0m
                });
            }

            var result = new
            {
                TotalSubscriptions = subscriptionList.Count,
                MonthlyTotal = subscriptionList
                    .Where(s => s.Status == SubscriptionStatus.Active)
                    .Sum(s => CalculateMonthlyEquivalent(s.Amount, s.Frequency)),
                ActiveCount = subscriptionList.Count(s => s.Status == SubscriptionStatus.Active),
                PausedCount = subscriptionList.Count(s => s.Status == SubscriptionStatus.Paused),
                CancelledCount = subscriptionList.Count(s => s.Status == SubscriptionStatus.Cancelled),
                Subscriptions = subscriptionList.Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.MerchantName,
                    s.Amount,
                    Frequency = s.Frequency.ToString(),
                    Category = s.Category.ToString(),
                    Status = s.Status.ToString(),
                    s.NextDueDate,
                    s.LastBilledDate,
                    s.Description,
                    MonthlyEquivalent = CalculateMonthlyEquivalent(s.Amount, s.Frequency)
                })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_subscriptions for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_upcoming_subscriptions", ReadOnly = true)]
    [Description("Get subscriptions due in the next N days. Use this to help users prepare for upcoming bills and plan their budget accordingly.")]
    public async Task<string> GetUpcomingSubscriptionsAsync(
        [Description("User ID to get upcoming subscriptions for")] int userId,
        [Description("Number of days to look ahead (default 30)")] int daysAhead = 30,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_upcoming_subscriptions called for user {UserId}, days={Days}", userId, daysAhead);
            
            var now = DateTime.UtcNow;
            var endDate = now.AddDays(daysAhead);
            
            var calendarData = await _subscriptionService.GetCalendarDataAsync(userId, now, endDate);
            var upcomingList = calendarData.ToList();

            if (!upcomingList.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = $"No subscription payments due in the next {daysAhead} days.",
                    TotalUpcoming = 0,
                    TotalAmount = 0m,
                    LookAheadDays = daysAhead
                });
            }

            // CalendarSubscriptionDto has Date, Subscriptions list, and TotalAmount per day
            var allSubscriptions = upcomingList.SelectMany(d => d.Subscriptions.Select(s => new { d.Date, Subscription = s })).ToList();

            var result = new
            {
                LookAheadDays = daysAhead,
                TotalUpcoming = allSubscriptions.Count,
                TotalAmount = upcomingList.Sum(d => d.TotalAmount),
                NextDueDate = allSubscriptions.Min(s => s.Date),
                ByCategory = allSubscriptions
                    .GroupBy(s => s.Subscription.Category)
                    .Select(g => new
                    {
                        Category = g.Key.ToString(),
                        Count = g.Count(),
                        Total = g.Sum(s => s.Subscription.Amount)
                    }),
                UpcomingBills = allSubscriptions
                    .OrderBy(s => s.Date)
                    .Select(s => new
                    {
                        s.Subscription.Id,
                        s.Subscription.Name,
                        s.Subscription.Amount,
                        DueDate = s.Date.ToString("yyyy-MM-dd"),
                        DaysUntilDue = (s.Date.Date - now.Date).Days,
                        Category = s.Subscription.Category.ToString(),
                        Frequency = s.Subscription.Frequency.ToString()
                    })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_upcoming_subscriptions for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_subscription_summary", ReadOnly = true)]
    [Description("Get a summary of the user's subscriptions including totals, categories breakdown, and spending trends. Perfect for financial overviews and budgeting advice.")]
    public async Task<string> GetSubscriptionSummaryAsync(
        [Description("User ID to get subscription summary for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_subscription_summary called for user {UserId}", userId);
            
            var summary = await _subscriptionService.GetSubscriptionSummaryAsync(userId);

            var result = new
            {
                summary.MonthlyTotal,
                summary.YearlyTotal,
                summary.ActiveCount,
                AveragePricePerSubscription = summary.ActiveCount > 0 
                    ? summary.MonthlyTotal / summary.ActiveCount 
                    : 0m,
                CategoryBreakdown = summary.CategoryBreakdown?.Select(c => new 
                { 
                    Category = c.Category.ToString(), 
                    c.Amount, 
                    c.Count 
                }),
                TopExpenseCategories = summary.CategoryBreakdown?
                    .OrderByDescending(c => c.Amount)
                    .Take(3)
                    .Select(c => new { Category = c.Category.ToString(), c.Amount, c.Count })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_subscription_summary for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_pending_subscription_suggestions", ReadOnly = true)]
    [Description("Get AI-detected subscription suggestions that the user hasn't reviewed yet. These are recurring payments we've identified that might be subscriptions.")]
    public async Task<string> GetPendingSubscriptionSuggestionsAsync(
        [Description("User ID to get pending suggestions for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_pending_subscription_suggestions called for user {UserId}", userId);
            
            var suggestions = await _suggestedSubscriptionRepository.GetPendingByUserIdAsync(userId);
            var suggestionList = suggestions.ToList();

            if (!suggestionList.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = "No pending subscription suggestions. The user has reviewed all detected subscriptions.",
                    TotalPending = 0
                });
            }

            var result = new
            {
                TotalPending = suggestionList.Count,
                HighConfidenceCount = suggestionList.Count(s => s.Confidence >= 0.8),
                TotalPotentialMonthly = suggestionList.Sum(s => CalculateMonthlyEquivalent(s.Amount, s.Frequency)),
                Suggestions = suggestionList
                    .OrderByDescending(s => s.Confidence)
                    .Select(s => new
                    {
                        s.Id,
                        MerchantName = s.DisplayMerchantName,
                        s.Amount,
                        Frequency = s.Frequency.ToString(),
                        ConfidencePercent = (s.Confidence * 100).ToString("F0") + "%",
                        s.AIInsight,
                        s.MatchedTransactionCount,
                        PredictedNextDate = s.PredictedNextDate?.ToString("yyyy-MM-dd"),
                        DetectedAt = s.DetectedAt.ToString("yyyy-MM-dd"),
                        HasCommunityVerification = s.CrowdsourceBoost > 0
                    })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_pending_subscription_suggestions for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "detect_subscriptions", ReadOnly = true)]
    [Description("Run subscription detection on the user's transaction history to find recurring payments that might be subscriptions. Use this when the user wants to find subscriptions they may have forgotten about, or when reviewing their recurring expenses. Returns both high-confidence matches and patterns that need review.")]
    public async Task<string> DetectSubscriptionsAsync(
        [Description("User ID to detect subscriptions for")] int userId,
        [Description("Minimum number of occurrences to detect (default 2)")] int minOccurrences = 2,
        [Description("Amount tolerance in dollars for grouping similar transactions (default 5.00)")] decimal amountTolerance = 5.00m,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: detect_subscriptions called for user {UserId}, minOccurrences={Min}, tolerance=${Tol}", 
                userId, minOccurrences, amountTolerance);
            
            var request = new SubscriptionDetectionRequest
            {
                MinOccurrences = minOccurrences,
                AmountTolerance = amountTolerance,
                IncludeAIAnalysis = true
            };
            
            var response = await _subscriptionService.DetectSubscriptionsAsync(userId, request);

            if (!response.DetectedSubscriptions.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = "No subscription patterns detected. This could mean the user doesn't have recurring payments, or their payments are too irregular to detect. Try asking about specific merchants they think might be subscriptions.",
                    TotalDetected = 0,
                    Suggestions = new[] {
                        "Ask the user if they have streaming services (Netflix, Spotify, etc.)",
                        "Check if they have gym memberships or fitness apps",
                        "Look for utility payments that might be variable amount"
                    }
                });
            }

            var result = new
            {
                TotalDetected = response.TotalDetected,
                HighConfidenceCount = response.HighConfidenceCount,
                PendingReviewCount = response.PendingReviewCount,
                IsAIEnhanced = response.IsAIEnhanced,
                TotalPotentialMonthlyCost = response.DetectedSubscriptions.Sum(s => CalculateMonthlyEquivalent(s.Amount, s.Frequency)),
                DetectedSubscriptions = response.DetectedSubscriptions
                    .OrderByDescending(s => s.Confidence)
                    .Select(s => new
                    {
                        s.MerchantName,
                        s.Amount,
                        Frequency = s.Frequency.ToString(),
                        ConfidencePercent = (s.Confidence * 100).ToString("F0") + "%",
                        ConfidenceClassification = s.ConfidenceDetails?.Classification ?? "Unknown",
                        s.IsSubscriptionLikely,
                        NextDue = s.EstimatedNextDue.ToString("yyyy-MM-dd"),
                        MatchedTransactions = s.RelatedTransactionIds.Count,
                        s.DetectionMethod,
                        SuggestedCategory = s.AISuggestedCategory?.ToString(),
                        s.AIInsight,
                        // Detailed confidence breakdown for AI reasoning
                        ConfidenceBreakdown = s.ConfidenceDetails != null ? new
                        {
                            IntervalRegularity = (s.ConfidenceDetails.IntervalRegularityScore * 100).ToString("F0") + "%",
                            AmountConsistency = (s.ConfidenceDetails.AmountConsistencyScore * 100).ToString("F0") + "%",
                            OccurrenceScore = (s.ConfidenceDetails.OccurrenceScore * 100).ToString("F0") + "%",
                            KnownMerchantBonus = s.ConfidenceDetails.KnownMerchantBoost > 0,
                            HasGapPenalty = s.ConfidenceDetails.GapPenalty < 0,
                            s.ConfidenceDetails.Explanation
                        } : null,
                        PriceChange = s.PriceChangeAmount.HasValue ? new
                        {
                            PreviousAmount = s.PreviousAmount,
                            ChangeAmount = s.PriceChangeAmount,
                            ChangePercent = s.PriceChangePercent
                        } : null
                    }),
                ReviewRecommendation = response.HighConfidenceCount > 0 
                    ? $"Found {response.HighConfidenceCount} high-confidence subscriptions that the user should review and confirm. Ask if they want to track these automatically."
                    : "All detected patterns need user review to confirm if they are subscriptions."
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in detect_subscriptions for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "analyze_merchant_for_subscription", ReadOnly = true)]
    [Description("Analyze a specific merchant name to find if it looks like a subscription in the user's transactions. Use this when the user mentions a specific service like 'Netflix' or 'my gym' and wants to know if they're being charged for it.")]
    public async Task<string> AnalyzeMerchantForSubscriptionAsync(
        [Description("User ID to analyze transactions for")] int userId,
        [Description("Merchant name to search for (e.g., 'Netflix', 'Spotify', 'Planet Fitness')")] string merchantName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: analyze_merchant_for_subscription called for user {UserId}, merchant={Merchant}", 
                userId, merchantName);
            
            // Run detection with very low thresholds to find even weak patterns
            var request = new SubscriptionDetectionRequest
            {
                MinOccurrences = 2,
                AmountTolerance = 10.00m, // Wide tolerance for this specific search
                IncludeAIAnalysis = true
            };
            
            var response = await _subscriptionService.DetectSubscriptionsAsync(userId, request);
            
            // Find matches for the merchant name (case-insensitive partial match)
            var normalizedSearch = merchantName.ToUpperInvariant();
            var matches = response.DetectedSubscriptions
                .Where(s => s.MerchantName.ToUpperInvariant().Contains(normalizedSearch) ||
                           normalizedSearch.Contains(s.MerchantName.ToUpperInvariant().Split(' ').FirstOrDefault() ?? ""))
                .ToList();

            if (!matches.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = $"No recurring payment patterns found for '{merchantName}'. This could mean: (1) The user doesn't have this subscription, (2) It's charged under a different name (like a parent company), or (3) They haven't had enough charges yet for us to detect a pattern.",
                    MerchantSearched = merchantName,
                    MatchesFound = 0,
                    Suggestions = new[] {
                        "Ask the user to check their recent transactions for this merchant",
                        "The charge might appear under a different name (e.g., 'DISNEY PLUS' instead of 'Disney+')",
                        "If it's a new subscription, wait for 2-3 billing cycles before detection"
                    }
                });
            }

            var result = new
            {
                MerchantSearched = merchantName,
                MatchesFound = matches.Count,
                Matches = matches.Select(s => new
                {
                    s.MerchantName,
                    s.Amount,
                    Frequency = s.Frequency.ToString(),
                    ConfidencePercent = (s.Confidence * 100).ToString("F0") + "%",
                    ConfidenceClassification = s.ConfidenceDetails?.Classification ?? "Unknown",
                    NextDue = s.EstimatedNextDue.ToString("yyyy-MM-dd"),
                    MatchedTransactions = s.RelatedTransactionIds.Count,
                    SuggestedCategory = s.AISuggestedCategory?.ToString(),
                    s.AIInsight,
                    // Detailed breakdown helps AI understand WHY confidence is high/low
                    ConfidenceBreakdown = s.ConfidenceDetails != null ? new
                    {
                        IntervalRegularity = (s.ConfidenceDetails.IntervalRegularityScore * 100).ToString("F0") + "%",
                        AmountConsistency = (s.ConfidenceDetails.AmountConsistencyScore * 100).ToString("F0") + "%",
                        OccurrenceScore = (s.ConfidenceDetails.OccurrenceScore * 100).ToString("F0") + "%",
                        s.ConfidenceDetails.Explanation
                    } : null,
                    Recommendation = s.ConfidenceDetails?.Classification switch
                    {
                        "VeryHigh" => "Very high confidence - this is almost certainly a subscription. Recommend auto-tracking.",
                        "High" => "High confidence - strong subscription pattern. Recommend tracking with user confirmation.",
                        "Medium" => "Medium confidence - likely a subscription but has some irregularities. Ask user to confirm.",
                        _ => "Low confidence - irregular pattern. Could be recurring spending rather than a subscription. User should review."
                    }
                })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in analyze_merchant_for_subscription for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate monthly equivalent amount based on frequency.
    /// </summary>
    private static decimal CalculateMonthlyEquivalent(decimal amount, SubscriptionFrequency frequency)
    {
        return frequency switch
        {
            SubscriptionFrequency.Weekly => amount * 4.33m,
            SubscriptionFrequency.BiWeekly => amount * 2.17m,
            SubscriptionFrequency.Monthly => amount,
            SubscriptionFrequency.Quarterly => amount / 3m,
            SubscriptionFrequency.SemiAnnual => amount / 6m,
            SubscriptionFrequency.Annual => amount / 12m,
            _ => amount
        };
    }
}
