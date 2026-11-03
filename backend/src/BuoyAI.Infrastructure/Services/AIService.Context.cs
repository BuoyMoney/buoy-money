using System.Text;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.Enums;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Partial class containing financial context building and utility methods:
/// - BuildFinancialContextAsync
/// - BuildApplicationContext  
/// - BuildUserProfileContext
/// - Token estimation and cost utilities
/// </summary>
public partial class AIService
{
    private async Task<string> BuildFinancialContextAsync(int userId, string context)
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
            
            // Get recent transactions
            var transactionFilter = new TransactionFilterRequest
            {
                StartDate = now.AddDays(-30),
                EndDate = now,
                PageSize = 20
            };
            
            var transactionResponse = await _transactionService.GetTransactionsWithPaginationAsync(userId, transactionFilter);
            
            if (transactionResponse?.Transactions?.Any() == true)
            {
                contextBuilder.AppendLine($"=== RECENT TRANSACTIONS ({now.AddDays(-30):MMM d} to {now:MMM d, yyyy}) ===");
                foreach (var transaction in transactionResponse.Transactions.Take(10))
                {
                    // Use TransactionType enum instead of sign for consistency
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
            var savingsGoals = await _savingsService.GetSavingsGoalsAsync(userId);
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build complete financial context for user {UserId}", userId);
            contextBuilder.AppendLine("=== FINANCIAL CONTEXT ===");
            contextBuilder.AppendLine("Financial data temporarily unavailable.");
        }
        
        return contextBuilder.ToString();
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

    private static string BuildUserProfileContext(UserProfileForAIDto? userProfile)
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

    private int EstimateTokens(string text)
    {
        // Rough estimation: ~4 characters per token
        return Math.Max(1, text.Length / 4);
    }

    // One price table for every call (see AiPricing), so chat rows and the cost tracker agree.
    private decimal CalculateCost(string model, int inputTokens, int outputTokens) =>
        BuoyAI.Infrastructure.Services.Ai.AiPricing.Cost(model, inputTokens, outputTokens, _logger);

    /// <summary>
    /// Searches past chat history for contextually relevant messages using semantic search.
    /// Returns a context string summarizing relevant past conversations.
    /// </summary>
    private async Task<string> BuildPastConversationContextAsync(int userId, string query, int maxResults = 3)
    {
        try
        {
            var relevantMessages = await _chatEmbeddingService.SearchChatHistoryAsync(userId, query, maxResults);
            
            if (relevantMessages == null || !relevantMessages.Any())
            {
                return string.Empty;
            }

            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("=== RELEVANT PAST CONVERSATIONS ===");
            contextBuilder.AppendLine("The following messages from previous conversations may provide helpful context:");
            contextBuilder.AppendLine();

            foreach (var message in relevantMessages)
            {
                var role = message.Role == "user" ? "User" : "Assistant";
                var date = message.CreatedAt.ToString("MMM d, yyyy");
                contextBuilder.AppendLine($"[{date}] {role}: {message.Content}");
            }

            contextBuilder.AppendLine();
            contextBuilder.AppendLine("Use this context to provide continuity in your responses, but focus on the current query.");
            contextBuilder.AppendLine();

            _logger.LogInformation("Added {Count} relevant past messages to context for user {UserId}", 
                relevantMessages.Count(), userId);

            return contextBuilder.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build past conversation context for user {UserId}", userId);
            return string.Empty;
        }
    }
}
