using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tools for reading budget data.
/// These tools provide budget context to the AI.
/// </summary>
[McpServerToolType]
public sealed class BudgetReadTools
{
    private readonly IBudgetService _budgetService;
    private readonly ICountUpBudgetService _countUpService;
    private readonly ILogger<BudgetReadTools> _logger;

    public BudgetReadTools(
        IBudgetService budgetService,
        ICountUpBudgetService countUpService,
        ILogger<BudgetReadTools> logger)
    {
        _budgetService = budgetService;
        _countUpService = countUpService;
        _logger = logger;
    }

    [McpServerTool(Name = "get_budgets", ReadOnly = true)]
    [Description(
        "Get the user's budgets: what they PLANNED to earn and spend per category, alongside actual " +
        "per-category spending. AllocatedAmount is a TARGET the user set — it is NOT money received " +
        "or spent, and must never be reported as such. For actual totals (income received, money " +
        "spent, net cash flow) use get_cash_flow instead. Use this tool to answer 'am I within " +
        "budget' and 'what did I plan for this category'.")]
    public async Task<string> GetBudgetsAsync(
        [Description("User ID to get budgets for")] int userId,
        [Description("Include archived/inactive budgets")] bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_budgets called for user {UserId}", userId);
            
            var budgets = await _budgetService.GetAccessibleBudgetsAsync(userId);
            var budgetList = budgets.ToList();

            if (!budgetList.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = "No budgets found. The user may want to create a budget to track their spending.",
                    TotalBudgets = 0
                });
            }

            var result = new
            {
                TotalBudgets = budgetList.Count,
                Budgets = budgetList.Select(b => new
                {
                    b.Id,
                    b.Name,
                    b.IsPrimary,
                    Type = b.Type.ToString(),
                    Period = new
                    {
                        StartDate = b.CurrentPeriodStart.ToString("yyyy-MM-dd"),
                        EndDate = b.CurrentPeriodEnd.ToString("yyyy-MM-dd"),
                        DaysRemaining = Math.Max(0, (b.CurrentPeriodEnd - DateTime.UtcNow).Days),
                        b.DaysElapsed,
                        b.DaysInCurrentPeriod
                    },
                    // Split income from expense. The old single TotalBudgeted summed both — an $8,000
                    // salary target plus $8,000 of expense targets came out as $16,000, a number that means nothing and invites exactly the
                    // misreading that shipped: the model quoted the two halves back as income
                    // received and money spent.
                    PlannedIncome = b.Categories?.Where(c => c.IsIncome).Sum(c => c.AllocatedAmount) ?? 0,
                    PlannedSpending = b.Categories?.Where(c => !c.IsIncome).Sum(c => c.AllocatedAmount) ?? 0,
                    ActualSpentAgainstBudget = b.Categories?.Where(c => !c.IsIncome).Sum(c => c.SpentAmount) ?? 0,
                    Note = "PlannedIncome/PlannedSpending are TARGETS the user set, not actuals. " +
                           "For money actually received or spent, call get_cash_flow.",
                    Categories = b.Categories?.Select(c => new
                    {
                        c.Id,
                        c.CategoryName,
                        c.AllocatedAmount,
                        c.SpentAmount,
                        c.RemainingAmount,
                        PercentUsed = c.PercentageUsed,
                        c.IsCountUpEnabled,
                        c.AllowRollover,
                        c.RolloverBalance,
                        c.EffectiveAmount,
                        Status = GetCategoryStatus(c)
                    })
                })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_budgets for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_budget_overview", ReadOnly = true)]
    [Description("Get a high-level overview of the user's primary budget with key metrics. Use this for quick budget status checks.")]
    public async Task<string> GetBudgetOverviewAsync(
        [Description("User ID to get budget overview for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_budget_overview called for user {UserId}", userId);
            
            var budget = await _budgetService.GetPrimaryBudgetAsync(userId);
            
            if (budget == null)
            {
                return JsonSerializer.Serialize(new
                {
                    Message = "No primary budget found. The user should create a budget to track spending.",
                    HasBudget = false
                });
            }

            var categories = budget.Categories?.ToList() ?? new List<BudgetCategoryDto>();
            var totalBudgeted = categories.Sum(c => c.AllocatedAmount);
            var totalSpent = categories.Sum(c => c.SpentAmount);
            var totalRemaining = categories.Sum(c => c.RemainingAmount);

            // Find categories that are over budget or close to limit
            var overBudget = categories.Where(c => c.SpentAmount > c.AllocatedAmount).ToList();
            var nearLimit = categories.Where(c => c.AllocatedAmount > 0 && 
                                                   c.SpentAmount <= c.AllocatedAmount && 
                                                   (c.SpentAmount / c.AllocatedAmount) >= 0.8m).ToList();

            var result = new
            {
                BudgetName = budget.Name,
                BudgetId = budget.Id,
                Period = new
                {
                    StartDate = budget.CurrentPeriodStart.ToString("yyyy-MM-dd"),
                    EndDate = budget.CurrentPeriodEnd.ToString("yyyy-MM-dd"),
                    DaysRemaining = Math.Max(0, (budget.CurrentPeriodEnd - DateTime.UtcNow).Days),
                    budget.DaysElapsed,
                    budget.DaysInCurrentPeriod
                },
                Summary = new
                {
                    TotalBudgeted = totalBudgeted,
                    TotalSpent = totalSpent,
                    TotalRemaining = totalRemaining,
                    PercentUsed = totalBudgeted > 0 ? Math.Round((totalSpent / totalBudgeted) * 100, 1) : 0,
                    CategoryCount = categories.Count
                },
                Alerts = new
                {
                    OverBudgetCategories = overBudget.Select(c => new { c.CategoryName, c.SpentAmount, c.AllocatedAmount, OverBy = c.SpentAmount - c.AllocatedAmount }),
                    NearLimitCategories = nearLimit.Select(c => new { c.CategoryName, c.SpentAmount, c.AllocatedAmount, PercentUsed = Math.Round((c.SpentAmount / c.AllocatedAmount) * 100, 1) })
                },
                TopSpendingCategories = categories
                    .OrderByDescending(c => c.SpentAmount)
                    .Take(5)
                    .Select(c => new { c.CategoryName, c.SpentAmount, c.AllocatedAmount })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_budget_overview for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_countup_categories", ReadOnly = true)]
    [Description("Get all count-up (savings-style) budget categories with their daily allowance and accumulated amounts. Count-up budgets accumulate daily allowances instead of having a fixed monthly limit.")]
    public async Task<string> GetCountUpCategoriesAsync(
        [Description("User ID to get count-up categories for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_countup_categories called for user {UserId}", userId);
            
            var countUpCategories = await _countUpService.GetAllCountUpCategoriesAsync(userId);
            var categoryList = countUpCategories.ToList();

            if (!categoryList.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = "No count-up categories found. Count-up budgeting allows categories to accumulate daily allowances, great for things like dining out or entertainment where saving up is beneficial.",
                    TotalCountUpCategories = 0
                });
            }

            var result = new
            {
                TotalCountUpCategories = categoryList.Count,
                Categories = categoryList.Select(c => new
                {
                    c.CategoryId,
                    c.CategoryName,
                    c.DailyAllowance,
                    c.EarnedToDate,
                    c.ActualSpent,
                    c.AvailableBalance,
                    c.TotalDays,
                    c.DaysElapsed,
                    RemainingDays = c.TotalDays - c.DaysElapsed,
                    c.ProjectedSavings,
                    c.IsOverEarned,
                    c.SpendingRate
                })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_countup_categories for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    private static string GetCategoryStatus(BudgetCategoryDto category)
    {
        if (category.AllocatedAmount <= 0) return "no_budget";
        
        var percentUsed = (category.SpentAmount / category.AllocatedAmount) * 100;
        
        return percentUsed switch
        {
            > 100 => "over_budget",
            >= 90 => "critical",
            >= 75 => "warning",
            _ => "on_track"
        };
    }
}
