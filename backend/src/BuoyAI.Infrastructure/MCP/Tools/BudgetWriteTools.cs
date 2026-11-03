using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tools for creating and modifying budgets.
/// All write operations return a confirmation request that should be shown to the user.
/// </summary>
[McpServerToolType]
public sealed class BudgetWriteTools
{
    private readonly IBudgetService _budgetService;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<BudgetWriteTools> _logger;

    public BudgetWriteTools(
        IBudgetService budgetService,
        IUserRepository userRepository,
        ILogger<BudgetWriteTools> logger)
    {
        _budgetService = budgetService;
        _userRepository = userRepository;
        _logger = logger;
    }

    [McpServerTool(Name = "create_budget_category")]
    [Description("Create a new budget category with a monthly allocation. This will add a category to the user's primary budget. Returns a confirmation request that should be shown to the user before executing.")]
    public async Task<string> CreateBudgetCategoryAsync(
        [Description("User ID creating the category")] int userId,
        [Description("Category name (e.g., 'Dogs', 'Groceries', 'Entertainment')")] string categoryName,
        [Description("Monthly budget amount in dollars")] decimal amount,
        [Description("Should unspent money roll over to next month? Default: true")] bool? enableRollover = null,
        [Description("Use count-up budgeting (daily allowances accumulate)? Default: true")] bool? enableCountUp = null,
        [Description("Set to true to execute the action, false to just preview")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: create_budget_category called for user {UserId}, category={Category}, amount={Amount}, confirmed={Confirmed}", 
                userId, categoryName, amount, confirmed);

            // Get user preferences for defaults
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                return JsonSerializer.Serialize(new { Error = "User not found" });
            }

            // Apply defaults based on user's AI interaction mode
            var rollover = enableRollover ?? true;  // Default: enable rollover
            var countUp = enableCountUp ?? true;    // Default: enable count-up (hallmark feature!)

            // Get or create primary budget
            var budget = await _budgetService.GetPrimaryBudgetAsync(userId);
            if (budget == null)
            {
                return JsonSerializer.Serialize(new
                {
                    RequiresAction = true,
                    ActionType = "create_budget_first",
                    Message = "No primary budget exists. Would you like me to create one first?",
                    SuggestedAction = "Create a new budget, then add this category to it."
                });
            }

            // Try to parse category name to TransactionCategory enum
            TransactionCategory? transactionCategory = null;
            foreach (TransactionCategory cat in Enum.GetValues(typeof(TransactionCategory)))
            {
                if (cat.ToString().Equals(categoryName, StringComparison.OrdinalIgnoreCase) ||
                    cat.ToString().Replace("_", "").Equals(categoryName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                {
                    transactionCategory = cat;
                    break;
                }
            }

            // Check if category already exists in budget
            var existingCategory = budget.Categories?.FirstOrDefault(c => 
                c.CategoryName.Equals(categoryName, StringComparison.OrdinalIgnoreCase) ||
                (transactionCategory.HasValue && c.CategoryId == (int)transactionCategory.Value));

            if (existingCategory != null)
            {
                return JsonSerializer.Serialize(new
                {
                    RequiresAction = true,
                    ActionType = "category_exists",
                    Message = $"A category named '{existingCategory.CategoryName}' already exists with ${existingCategory.AllocatedAmount:F2} allocated.",
                    ExistingCategory = new
                    {
                        existingCategory.Id,
                        existingCategory.CategoryName,
                        existingCategory.AllocatedAmount
                    },
                    SuggestedAction = "Would you like to update the existing category amount instead?"
                });
            }

            // Build confirmation response
            var confirmation = new
            {
                RequiresConfirmation = !confirmed,
                ActionType = "create_budget_category",
                Preview = new
                {
                    CategoryName = categoryName,
                    MonthlyAmount = amount,
                    EnableRollover = rollover,
                    EnableCountUp = countUp,
                    BudgetName = budget.Name,
                    BudgetId = budget.Id
                },
                Description = $"Create '{categoryName}' budget category with ${amount:F2}/month" +
                             (rollover ? " (unspent rolls over)" : "") +
                             (countUp ? " (count-up enabled)" : ""),
                UIOptions = new
                {
                    ShowRolloverToggle = true,
                    ShowCountUpToggle = true,
                    RolloverDefault = rollover,
                    CountUpDefault = countUp,
                    RolloverDescription = "Unspent money carries over to next month",
                    CountUpDescription = "Accumulate daily allowances instead of fixed limit"
                }
            };

            if (!confirmed)
            {
                return JsonSerializer.Serialize(confirmation, new JsonSerializerOptions { WriteIndented = true });
            }

            // Execute the creation
            var categoryRequest = new CreateBudgetCategoryRequest
            {
                // Use unified CategoryId - system categories are 1-99, custom are 1000+
                CategoryId = transactionCategory.HasValue ? (int)transactionCategory.Value : (int)TransactionCategory.Uncategorized,
                AllocatedAmount = amount,
                AllowRollover = rollover,
                IsIncome = false
            };

            // For now, we'll update the existing budget with the new category
            // This requires adding the category to the budget's categories and updating
            var updateRequest = new CreateBudgetRequest
            {
                Name = budget.Name,
                Period = budget.Period,
                AutoReset = budget.AutoReset,
                TotalIncome = budget.TotalIncome,
                Categories = budget.Categories?.Select(c => new CreateBudgetCategoryRequest
                {
                    CategoryId = c.CategoryId, // Use unified CategoryId
                    AllocatedAmount = c.AllocatedAmount,
                    AllowRollover = c.AllowRollover,
                    IsIncome = c.IsIncome
                }).ToList() ?? new List<CreateBudgetCategoryRequest>()
            };
            
            updateRequest.Categories.Add(categoryRequest);

            var updatedBudget = await _budgetService.UpdateBudgetAsync(budget.Id, userId, updateRequest);

            var newCategory = updatedBudget.Categories?.FirstOrDefault(c => 
                c.CategoryId == categoryRequest.CategoryId &&
                c.AllocatedAmount == amount);

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Created '{categoryName}' budget category with ${amount:F2}/month!",
                Category = newCategory != null ? new
                {
                    newCategory.Id,
                    newCategory.CategoryName,
                    newCategory.AllocatedAmount,
                    newCategory.AllowRollover,
                    newCategory.IsCountUpEnabled
                } : null,
                BudgetId = updatedBudget.Id
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in create_budget_category for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "update_budget_amount")]
    [Description("Update the allocated amount for an existing budget category. Returns a confirmation request.")]
    public async Task<string> UpdateBudgetAmountAsync(
        [Description("User ID updating the category")] int userId,
        [Description("Budget category ID to update")] int categoryId,
        [Description("New monthly budget amount in dollars")] decimal newAmount,
        [Description("Apply this amount to future periods as well")] bool applyToFuture = false,
        [Description("Set to true to execute the action, false to just preview")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: update_budget_amount called for user {UserId}, categoryId={CategoryId}, newAmount={Amount}, confirmed={Confirmed}", 
                userId, categoryId, newAmount, confirmed);

            // Get the budget containing this category
            var budgets = await _budgetService.GetAccessibleBudgetsAsync(userId);
            BudgetCategoryDto? category = null;
            BudgetDto? parentBudget = null;

            foreach (var budget in budgets)
            {
                category = budget.Categories?.FirstOrDefault(c => c.Id == categoryId);
                if (category != null)
                {
                    parentBudget = budget;
                    break;
                }
            }

            if (category == null || parentBudget == null)
            {
                return JsonSerializer.Serialize(new { Error = "Budget category not found or you don't have access to it." });
            }

            var confirmation = new
            {
                RequiresConfirmation = !confirmed,
                ActionType = "update_budget_amount",
                Preview = new
                {
                    CategoryName = category.CategoryName,
                    CurrentAmount = category.AllocatedAmount,
                    NewAmount = newAmount,
                    Change = newAmount - category.AllocatedAmount,
                    ChangePercent = category.AllocatedAmount > 0 
                        ? Math.Round(((newAmount - category.AllocatedAmount) / category.AllocatedAmount) * 100, 1) 
                        : 0,
                    ApplyToFuture = applyToFuture,
                    BudgetName = parentBudget.Name
                },
                Description = $"Change '{category.CategoryName}' from ${category.AllocatedAmount:F2} to ${newAmount:F2}/month" +
                             (applyToFuture ? " (also applies to future periods)" : "")
            };

            if (!confirmed)
            {
                return JsonSerializer.Serialize(confirmation, new JsonSerializerOptions { WriteIndented = true });
            }

            // Execute the update. UpdateBudgetCategoryAsync overwrites the category from EVERY
            // field on the request, so we must carry the category's CURRENT rollover / count-up
            // configuration through — otherwise changing only the amount silently resets
            // AllowRollover, the rollover multiplier/overflow goal, the count-up rollover mode and
            // the category savings goal to their defaults (a data-loss footgun for a user who just
            // said "set Groceries to $600").
            var updateRequest = new UpdateBudgetCategoryRequest
            {
                AllocatedAmount = newAmount,
                ApplyToFuturePeriods = applyToFuture,
                AllowRollover = category.AllowRollover,
                MaxRolloverMultiplier = category.MaxRolloverMultiplier,
                RolloverOverflowSavingsGoalId = category.RolloverOverflowSavingsGoalId,
                CountUpRolloverMode = category.CountUpRolloverMode,
                SavingsGoalId = category.SavingsGoalId
            };

            var updatedCategory = await _budgetService.UpdateBudgetCategoryAsync(categoryId, updateRequest);

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Updated '{updatedCategory.CategoryName}' to ${newAmount:F2}/month!",
                Category = new
                {
                    updatedCategory.Id,
                    updatedCategory.CategoryName,
                    updatedCategory.AllocatedAmount,
                    updatedCategory.SpentAmount,
                    updatedCategory.RemainingAmount
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in update_budget_amount for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "toggle_category_settings")]
    [Description("Toggle rollover or count-up settings for a budget category.")]
    public async Task<string> ToggleCategorySettingsAsync(
        [Description("User ID updating the settings")] int userId,
        [Description("Budget category ID to update")] int categoryId,
        [Description("Enable rollover (unspent money carries over)")] bool? enableRollover = null,
        [Description("Enable count-up budgeting")] bool? enableCountUp = null,
        [Description("Set to true to execute the action")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: toggle_category_settings called for user {UserId}, categoryId={CategoryId}", 
                userId, categoryId);

            var budgets = await _budgetService.GetAccessibleBudgetsAsync(userId);
            BudgetCategoryDto? category = null;
            BudgetDto? parentBudget = null;

            foreach (var budget in budgets)
            {
                category = budget.Categories?.FirstOrDefault(c => c.Id == categoryId);
                if (category != null)
                {
                    parentBudget = budget;
                    break;
                }
            }

            if (category == null || parentBudget == null)
            {
                return JsonSerializer.Serialize(new { Error = "Budget category not found." });
            }

            var newRollover = enableRollover ?? category.AllowRollover;
            var newCountUp = enableCountUp ?? category.IsCountUpEnabled;

            var changes = new List<string>();
            if (enableRollover.HasValue && enableRollover.Value != category.AllowRollover)
                changes.Add(enableRollover.Value ? "Enable rollover" : "Disable rollover");
            if (enableCountUp.HasValue && enableCountUp.Value != category.IsCountUpEnabled)
                changes.Add(enableCountUp.Value ? "Enable count-up" : "Disable count-up");

            if (!changes.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    NoChanges = true,
                    Message = "No changes to make - settings are already at the requested values.",
                    CurrentSettings = new
                    {
                        category.CategoryName,
                        Rollover = category.AllowRollover,
                        CountUp = category.IsCountUpEnabled
                    }
                });
            }

            var confirmation = new
            {
                RequiresConfirmation = !confirmed,
                ActionType = "toggle_category_settings",
                Preview = new
                {
                    category.CategoryName,
                    Changes = changes,
                    NewSettings = new
                    {
                        Rollover = newRollover,
                        CountUp = newCountUp
                    }
                },
                Description = $"Update '{category.CategoryName}' settings: {string.Join(", ", changes)}"
            };

            if (!confirmed)
            {
                return JsonSerializer.Serialize(confirmation, new JsonSerializerOptions { WriteIndented = true });
            }

            // Execute the update
            var updateRequest = new UpdateBudgetCategoryRequest
            {
                AllocatedAmount = category.AllocatedAmount,
                AllowRollover = newRollover
            };

            var updatedCategory = await _budgetService.UpdateBudgetCategoryAsync(categoryId, updateRequest);

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Updated '{updatedCategory.CategoryName}' settings!",
                NewSettings = new
                {
                    Rollover = updatedCategory.AllowRollover,
                    CountUp = updatedCategory.IsCountUpEnabled
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in toggle_category_settings for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }
}
