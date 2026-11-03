using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tools for reading income data and patterns.
/// Provides AI access to income transactions, salary patterns, and recurring income detection.
/// </summary>
[McpServerToolType]
public sealed class IncomeReadTools
{
    private readonly ITransactionService _transactionService;
    private readonly ILogger<IncomeReadTools> _logger;
    
    private static readonly TransactionCategory[] IncomeCategories = 
    {
        TransactionCategory.Salary,
        TransactionCategory.Bonus,
        TransactionCategory.Investment,
        TransactionCategory.OtherIncome
    };

    public IncomeReadTools(
        ITransactionService transactionService,
        ILogger<IncomeReadTools> logger)
    {
        _transactionService = transactionService;
        _logger = logger;
    }

    [McpServerTool(Name = "get_income_summary", ReadOnly = true)]
    [Description("Get a summary of the user's income including recent income transactions and totals by source. Use this to understand the user's income patterns and sources.")]
    public async Task<string> GetIncomeSummaryAsync(
        [Description("User ID to get income summary for")] int userId,
        [Description("Number of days of income history to analyze (default: 90)")] int days = 90,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_income_summary called for user {UserId}, days={Days}", userId, days);
            
            var startDate = DateTime.UtcNow.AddDays(-days);
            var endDate = DateTime.UtcNow;

            var allIncomeTransactions = new List<TransactionDto>();
            
            // Get transactions for each income category
            foreach (var category in IncomeCategories)
            {
                var filter = new TransactionFilterRequest
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CategoryId = (int)category,
                    PageSize = 500,
                    Page = 1
                };
                
                var transactions = await _transactionService.GetTransactionsAsync(userId, filter);
                allIncomeTransactions.AddRange(transactions);
            }

            if (!allIncomeTransactions.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = $"No income transactions found in the last {days} days. The user may need to categorize their income transactions or connect bank accounts where their income is deposited.",
                    TotalIncome = 0m,
                    Period = new { StartDate = startDate.ToString("yyyy-MM-dd"), EndDate = endDate.ToString("yyyy-MM-dd") }
                });
            }

            // Income transactions typically have negative amounts in our system (credits to account)
            var incomeTransactions = allIncomeTransactions
                .OrderByDescending(t => t.Date)
                .ToList();

            var bySource = incomeTransactions
                .GroupBy(t => t.CategoryName)
                .Select(g => new
                {
                    Source = g.Key,
                    TransactionCount = g.Count(),
                    TotalAmount = g.Sum(t => Math.Abs(t.Amount)),
                    AverageAmount = g.Average(t => Math.Abs(t.Amount)),
                    LastDate = g.Max(t => t.Date).ToString("yyyy-MM-dd"),
                    Merchants = g.GroupBy(t => t.MerchantName)
                        .Select(m => new { Name = m.Key, Count = m.Count(), Total = m.Sum(t => Math.Abs(t.Amount)) })
                        .OrderByDescending(m => m.Total)
                        .Take(3)
                })
                .OrderByDescending(s => s.TotalAmount)
                .ToList();

            var result = new
            {
                Period = new { StartDate = startDate.ToString("yyyy-MM-dd"), EndDate = endDate.ToString("yyyy-MM-dd") },
                TotalIncome = incomeTransactions.Sum(t => Math.Abs(t.Amount)),
                TransactionCount = incomeTransactions.Count,
                MonthlyAverage = incomeTransactions.Sum(t => Math.Abs(t.Amount)) / (days / 30.0m),
                BySource = bySource,
                RecentIncomeTransactions = incomeTransactions
                    .Take(10)
                    .Select(t => new
                    {
                        Date = t.Date.ToString("yyyy-MM-dd"),
                        t.MerchantName,
                        Amount = Math.Abs(t.Amount),
                        Source = t.CategoryName,
                        t.Description
                    })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_income_summary for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_recurring_income", ReadOnly = true)]
    [Description("Detect recurring income patterns like paychecks and regular deposits. Use this to understand the user's regular income schedule and amounts.")]
    public async Task<string> GetRecurringIncomeAsync(
        [Description("User ID to analyze recurring income for")] int userId,
        [Description("Number of days of history to analyze (default: 180)")] int days = 180,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_recurring_income called for user {UserId}, days={Days}", userId, days);
            
            var startDate = DateTime.UtcNow.AddDays(-days);
            var endDate = DateTime.UtcNow;

            // Get salary transactions specifically (most likely to be regular)
            var filter = new TransactionFilterRequest
            {
                StartDate = startDate,
                EndDate = endDate,
                CategoryId = (int)TransactionCategory.Salary,
                PageSize = 500,
                Page = 1
            };
            
            var salaryTransactions = (await _transactionService.GetTransactionsAsync(userId, filter)).ToList();

            // Group by merchant to find patterns
            var merchantPatterns = salaryTransactions
                .GroupBy(t => t.MerchantName?.ToLowerInvariant() ?? "unknown")
                .Select(g => AnalyzeIncomePattern(g.Key, g.ToList()))
                .Where(p => p.TransactionCount >= 2) // Need at least 2 occurrences to detect pattern
                .OrderByDescending(p => p.Confidence)
                .ToList();

            if (!merchantPatterns.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = $"No recurring income patterns detected in the last {days} days. This could mean: 1) The user doesn't have regular income yet, 2) Income transactions need to be categorized as 'Salary', or 3) Need more transaction history.",
                    DetectedPatterns = 0,
                    Suggestions = new[]
                    {
                        "Review transactions and categorize income as 'Salary' or 'Bonus'",
                        "Connect bank accounts where paychecks are deposited",
                        "Allow more time for pattern detection (needs 2+ similar transactions)"
                    }
                });
            }

            var result = new
            {
                AnalysisPeriod = new { StartDate = startDate.ToString("yyyy-MM-dd"), EndDate = endDate.ToString("yyyy-MM-dd") },
                DetectedPatterns = merchantPatterns.Count,
                TotalMonthlyIncome = merchantPatterns.Sum(p => p.EstimatedMonthlyAmount),
                RecurringIncomePatterns = merchantPatterns.Select(p => new
                {
                    p.MerchantName,
                    p.TransactionCount,
                    p.AverageAmount,
                    p.Frequency,
                    p.EstimatedMonthlyAmount,
                    p.ConfidenceLevel,
                    p.LastPaymentDate,
                    p.PredictedNextPaymentDate,
                    p.AmountVariation
                })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_recurring_income for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_next_income", ReadOnly = true)]
    [Description("Predict when the user's next paycheck or regular income will arrive based on detected patterns. Useful for cash flow planning.")]
    public async Task<string> GetNextIncomeAsync(
        [Description("User ID to predict next income for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_next_income called for user {UserId}", userId);
            
            // Analyze last 180 days for patterns
            var startDate = DateTime.UtcNow.AddDays(-180);
            var endDate = DateTime.UtcNow;

            var filter = new TransactionFilterRequest
            {
                StartDate = startDate,
                EndDate = endDate,
                CategoryId = (int)TransactionCategory.Salary,
                PageSize = 500,
                Page = 1
            };
            
            var salaryTransactions = (await _transactionService.GetTransactionsAsync(userId, filter)).ToList();

            var merchantPatterns = salaryTransactions
                .GroupBy(t => t.MerchantName?.ToLowerInvariant() ?? "unknown")
                .Select(g => AnalyzeIncomePattern(g.Key, g.ToList()))
                .Where(p => p.TransactionCount >= 2 && p.Confidence >= 0.5)
                .OrderByDescending(p => p.Confidence)
                .ToList();

            if (!merchantPatterns.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = "Unable to predict next income - no regular income patterns detected. Need at least 2 salary/income transactions from the same source to establish a pattern.",
                    HasPrediction = false
                });
            }

            var predictions = merchantPatterns
                .Where(p => p.PredictedNextPaymentDate.HasValue && p.PredictedNextPaymentDate > DateTime.UtcNow)
                .OrderBy(p => p.PredictedNextPaymentDate)
                .Select(p => new
                {
                    p.MerchantName,
                    ExpectedDate = p.PredictedNextPaymentDate?.ToString("yyyy-MM-dd"),
                    DaysUntil = p.PredictedNextPaymentDate.HasValue 
                        ? (p.PredictedNextPaymentDate.Value.Date - DateTime.UtcNow.Date).Days 
                        : (int?)null,
                    ExpectedAmount = p.AverageAmount,
                    p.ConfidenceLevel
                })
                .ToList();

            if (!predictions.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = "Detected income patterns but cannot predict next payment - the last payment may have been recent or the pattern is unclear.",
                    HasPrediction = false,
                    DetectedPatterns = merchantPatterns.Count,
                    LastKnownPayments = merchantPatterns.Select(p => new
                    {
                        p.MerchantName,
                        LastDate = p.LastPaymentDate?.ToString("yyyy-MM-dd"),
                        Amount = p.AverageAmount
                    })
                });
            }

            var nextIncome = predictions.First();
            
            return JsonSerializer.Serialize(new
            {
                HasPrediction = true,
                NextExpectedIncome = nextIncome,
                TotalExpectedThisMonth = predictions
                    .Where(p => DateTime.TryParse(p.ExpectedDate, out var d) && d.Month == DateTime.UtcNow.Month)
                    .Sum(p => p.ExpectedAmount),
                AllUpcomingIncome = predictions
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_next_income for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Analyze transactions from a single merchant to detect income patterns.
    /// </summary>
    private static IncomePattern AnalyzeIncomePattern(string merchantName, List<TransactionDto> transactions)
    {
        if (transactions.Count < 2)
        {
            return new IncomePattern
            {
                MerchantName = merchantName,
                TransactionCount = transactions.Count,
                Confidence = 0
            };
        }

        var sortedTransactions = transactions.OrderBy(t => t.Date).ToList();
        var amounts = sortedTransactions.Select(t => Math.Abs(t.Amount)).ToList();
        var dates = sortedTransactions.Select(t => t.Date).ToList();

        // Calculate average days between transactions
        var intervals = new List<int>();
        for (int i = 1; i < dates.Count; i++)
        {
            intervals.Add((dates[i].Date - dates[i - 1].Date).Days);
        }

        var avgInterval = intervals.Count > 0 ? intervals.Average() : 0;
        var avgAmount = amounts.Average();
        var amountStdDev = CalculateStdDev(amounts);
        var intervalStdDev = intervals.Count > 0 ? CalculateStdDev(intervals.Select(i => (decimal)i).ToList()) : 0;

        // Determine frequency
        var frequency = avgInterval switch
        {
            >= 1 and <= 10 => "Weekly",
            >= 11 and <= 18 => "Bi-weekly",
            >= 19 and <= 35 => "Monthly",
            >= 36 and <= 62 => "Bi-monthly",
            _ => "Irregular"
        };

        // Calculate confidence based on consistency
        var amountConsistency = avgAmount > 0 ? Math.Max(0, 1 - ((double)amountStdDev / (double)avgAmount)) : 0;
        var intervalConsistency = avgInterval > 0 ? Math.Max(0, 1 - ((double)intervalStdDev / avgInterval)) : 0;
        var confidence = (amountConsistency + intervalConsistency) / 2;

        // Predict next payment
        var lastDate = dates.Last();
        var predictedNext = avgInterval > 0 ? lastDate.AddDays(avgInterval) : (DateTime?)null;

        // Calculate estimated monthly amount
        var monthlyMultiplier = frequency switch
        {
            "Weekly" => 4.33m,
            "Bi-weekly" => 2.17m,
            "Monthly" => 1m,
            "Bi-monthly" => 0.5m,
            _ => 1m
        };

        return new IncomePattern
        {
            MerchantName = merchantName,
            TransactionCount = transactions.Count,
            AverageAmount = Math.Round(avgAmount, 2),
            Frequency = frequency,
            EstimatedMonthlyAmount = Math.Round(avgAmount * monthlyMultiplier, 2),
            Confidence = Math.Round(confidence, 2),
            ConfidenceLevel = confidence >= 0.8 ? "High" : confidence >= 0.5 ? "Medium" : "Low",
            LastPaymentDate = lastDate,
            PredictedNextPaymentDate = predictedNext,
            AmountVariation = amountStdDev > 0 ? $"±${Math.Round(amountStdDev, 2)}" : "Consistent"
        };
    }

    private static decimal CalculateStdDev(List<decimal> values)
    {
        if (values.Count <= 1) return 0;
        
        var avg = values.Average();
        var sumOfSquares = values.Sum(v => (v - avg) * (v - avg));
        return (decimal)Math.Sqrt((double)(sumOfSquares / (values.Count - 1)));
    }

    private class IncomePattern
    {
        public string MerchantName { get; set; } = string.Empty;
        public int TransactionCount { get; set; }
        public decimal AverageAmount { get; set; }
        public string Frequency { get; set; } = "Unknown";
        public decimal EstimatedMonthlyAmount { get; set; }
        public double Confidence { get; set; }
        public string ConfidenceLevel { get; set; } = "Low";
        public DateTime? LastPaymentDate { get; set; }
        public DateTime? PredictedNextPaymentDate { get; set; }
        public string AmountVariation { get; set; } = "Unknown";
    }
}
