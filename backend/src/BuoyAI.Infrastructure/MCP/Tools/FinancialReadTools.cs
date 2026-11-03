using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tools for reading financial data (accounts, transactions, etc.)
/// These tools provide context to the AI for answering user questions.
/// </summary>
[McpServerToolType]
public sealed class FinancialReadTools
{
    // Read straight from the database. This used to go through IPlaidService, which exists only
    // on hosted; the accounts are the same rows either way.
    private readonly IAccountRepository _accountRepository;
    private readonly IUserRepository _userRepository;
    private readonly ITransactionService _transactionService;
    private readonly ILogger<FinancialReadTools> _logger;

    public FinancialReadTools(
        IAccountRepository accountRepository,
        IUserRepository userRepository,
        ITransactionService transactionService,
        ILogger<FinancialReadTools> logger)
    {
        _accountRepository = accountRepository;
        _userRepository = userRepository;
        _transactionService = transactionService;
        _logger = logger;
    }

    [McpServerTool(Name = "get_accounts", ReadOnly = true)]
    [Description("Get the user's connected bank accounts with current balances. Use this to understand the user's financial accounts.")]
    public async Task<string> GetAccountsAsync(
        [Description("User ID to get accounts for")] int userId,
        [Description("Include inactive accounts")] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_accounts called for user {UserId}", userId);
            
            // Family-aware, as the accounts screen is: a solo user (null FamilyId) gets their own.
            var user = await _userRepository.GetByIdAsync(userId);
            var accounts = (await _accountRepository.GetByUserOrFamilyAsync(userId, user?.FamilyId))
                .Select(a => new
                {
                    a.Id,
                    a.Name,
                    a.Nickname,
                    a.Type,
                    a.Subtype,
                    a.CurrentBalance,
                    a.AvailableBalance,
                    a.Currency,
                    a.IsActive,
                    OwnerName = (string?)null
                });
            
            if (!includeInactive)
            {
                accounts = accounts.Where(a => a.IsActive);
            }

            var accountList = accounts.ToList();
            
            if (!accountList.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = "No connected accounts found. The user may need to connect their bank accounts first.",
                    TotalAccounts = 0
                });
            }

            var result = new
            {
                TotalAccounts = accountList.Count,
                Accounts = accountList.Select(a => new
                {
                    a.Id,
                    a.Name,
                    a.Nickname,
                    DisplayName = !string.IsNullOrEmpty(a.Nickname) ? a.Nickname : a.Name,
                    Type = a.Type.ToString(),
                    a.Subtype,
                    a.CurrentBalance,
                    a.AvailableBalance,
                    a.Currency,
                    a.IsActive,
                    a.OwnerName
                })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_accounts for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_transactions", ReadOnly = true)]
    [Description("Get recent transactions with optional filtering. Use this to analyze spending patterns or find specific transactions.")]
    public async Task<string> GetTransactionsAsync(
        [Description("User ID to get transactions for")] int userId,
        [Description("Number of days of transactions to retrieve (default: 30)")] int days = 30,
        [Description("Filter by category name (e.g., 'Groceries', 'DiningOut')")] string? category = null,
        [Description("Search term to filter transactions by merchant or description")] string? search = null,
        [Description("Maximum number of transactions to return (default: 50)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_transactions called for user {UserId}, days={Days}", userId, days);
            
            var startDate = DateTime.UtcNow.AddDays(-days);
            var endDate = DateTime.UtcNow;

            var filter = new TransactionFilterRequest
            {
                StartDate = startDate,
                EndDate = endDate,
                SearchTerm = search,
                PageSize = limit,
                Page = 1
            };

            // Parse category if provided
            if (!string.IsNullOrEmpty(category) && 
                Enum.TryParse<BuoyAI.Core.Enums.TransactionCategory>(category, true, out var parsedCategory))
            {
                filter.CategoryId = (int)parsedCategory;
            }

            var transactions = await _transactionService.GetTransactionsAsync(userId, filter);
            var transactionList = transactions.Take(limit).ToList();

            if (!transactionList.Any())
            {
                return $"No transactions found for the past {days} days" + 
                       (!string.IsNullOrEmpty(category) ? $" in category '{category}'" : "") +
                       (!string.IsNullOrEmpty(search) ? $" matching '{search}'" : "");
            }

            var result = new
            {
                Period = new { StartDate = startDate.ToString("yyyy-MM-dd"), EndDate = endDate.ToString("yyyy-MM-dd") },
                TotalTransactions = transactionList.Count,
                TotalSpent = transactionList.Where(t => t.Amount > 0).Sum(t => t.Amount),
                TotalIncome = transactionList.Where(t => t.Amount < 0).Sum(t => Math.Abs(t.Amount)),
                Transactions = transactionList.Select(t => new
                {
                    t.Id,
                    t.MerchantName,
                    t.Description,
                    Amount = t.Amount,
                    Date = t.Date.ToString("yyyy-MM-dd"),
                    Category = t.CategoryName,
                    t.AccountName,
                    IsIncome = t.Amount < 0
                })
            };

            return System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_transactions for user {UserId}", userId);
            return $"Error retrieving transactions: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_spending_summary", ReadOnly = true)]
    [Description("Get a spending summary broken down by category for a time period. Use this to understand where money is going.")]
    public async Task<string> GetSpendingSummaryAsync(
        [Description("User ID to get spending summary for")] int userId,
        [Description("Time period: 'week', 'month', 'quarter', or 'year' (default: month)")] string period = "month",
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_spending_summary called for user {UserId}, period={Period}", userId, period);
            
            var endDate = DateTime.UtcNow;
            var startDate = period.ToLower() switch
            {
                "week" => endDate.AddDays(-7),
                "month" => endDate.AddMonths(-1),
                "quarter" => endDate.AddMonths(-3),
                "year" => endDate.AddYears(-1),
                _ => endDate.AddMonths(-1)
            };

            var summary = await _transactionService.GetTransactionSummaryAsync(userId, startDate, endDate);
            var categoryStats = await _transactionService.GetCategoryStatisticsAsync(userId, startDate, endDate);

            // TotalExpenses is already positive in the summary
            var totalSpending = Math.Abs(summary.TotalExpenses);
            
            var result = new
            {
                Period = new { Start = startDate.ToString("yyyy-MM-dd"), End = endDate.ToString("yyyy-MM-dd"), Name = period },
                TotalSpending = totalSpending,
                TotalIncome = summary.TotalIncome,
                NetCashFlow = summary.NetAmount,
                TransactionCount = summary.TransactionCount,
                CategoryBreakdown = categoryStats.OrderByDescending(c => Math.Abs(c.TotalAmount)).Select(c => new
                {
                    Category = c.CategoryName,
                    TotalSpent = Math.Abs(c.TotalAmount),
                    c.TransactionCount,
                    PercentageOfTotal = totalSpending > 0 
                        ? Math.Round((Math.Abs(c.TotalAmount) / totalSpending) * 100, 1) 
                        : 0
                })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_spending_summary for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }
}
