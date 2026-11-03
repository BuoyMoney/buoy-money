using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tools for creating and modifying savings goals.
/// All write operations return a confirmation request.
/// </summary>
[McpServerToolType]
public sealed class SavingsWriteTools
{
    private readonly ISavingsService _savingsService;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<SavingsWriteTools> _logger;

    public SavingsWriteTools(
        ISavingsService savingsService,
        IUserRepository userRepository,
        ILogger<SavingsWriteTools> logger)
    {
        _savingsService = savingsService;
        _userRepository = userRepository;
        _logger = logger;
    }

    [McpServerTool(Name = "create_savings_goal")]
    [Description("Create a new savings goal with a target amount and optional target date. Returns a confirmation request.")]
    public async Task<string> CreateSavingsGoalAsync(
        [Description("User ID creating the goal")] int userId,
        [Description("Name of the savings goal (e.g., 'Vacation Fund', 'Emergency Fund')")] string name,
        [Description("Target amount to save in dollars")] decimal targetAmount,
        [Description("Target date to reach the goal (format: YYYY-MM-DD)")] string? targetDate = null,
        [Description("Description of what this savings goal is for")] string? description = null,
        [Description("Percentage of budget leftover to automatically allocate (0-100)")] decimal allocationPercentage = 0,
        [Description("Set to true to execute the action")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: create_savings_goal called for user {UserId}, name={Name}, target={Target}, confirmed={Confirmed}", 
                userId, name, targetAmount, confirmed);

            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                return JsonSerializer.Serialize(new { Error = "User not found" });
            }

            // Parse target date if provided
            DateTime? parsedTargetDate = null;
            if (!string.IsNullOrEmpty(targetDate))
            {
                if (DateTime.TryParse(targetDate, out var date))
                {
                    parsedTargetDate = date;
                }
                else
                {
                    return JsonSerializer.Serialize(new { Error = $"Invalid date format: {targetDate}. Please use YYYY-MM-DD format." });
                }
            }

            // Check for existing goal with same name
            var existingGoals = await _savingsService.GetSavingsGoalsAsync(userId);
            var existingGoal = existingGoals.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            
            if (existingGoal != null)
            {
                return JsonSerializer.Serialize(new
                {
                    RequiresAction = true,
                    ActionType = "goal_exists",
                    Message = $"A savings goal named '{existingGoal.Name}' already exists with ${existingGoal.CurrentAmount:F2} saved toward ${existingGoal.TargetAmount:F2}.",
                    ExistingGoal = new
                    {
                        existingGoal.Id,
                        existingGoal.Name,
                        existingGoal.CurrentAmount,
                        existingGoal.TargetAmount
                    },
                    SuggestedAction = "Would you like to add to the existing goal or create a new one with a different name?"
                });
            }

            // Calculate days until target and required daily savings
            int? daysUntilTarget = null;
            decimal? dailyRequired = null;
            if (parsedTargetDate.HasValue)
            {
                daysUntilTarget = Math.Max(1, (parsedTargetDate.Value - DateTime.UtcNow).Days);
                dailyRequired = Math.Round(targetAmount / daysUntilTarget.Value, 2);
            }

            var confirmation = new
            {
                RequiresConfirmation = !confirmed,
                ActionType = "create_savings_goal",
                Preview = new
                {
                    Name = name,
                    TargetAmount = targetAmount,
                    TargetDate = parsedTargetDate?.ToString("yyyy-MM-dd"),
                    Description = description ?? $"Saving for {name}",
                    AllocationPercentage = allocationPercentage,
                    DaysUntilTarget = daysUntilTarget,
                    DailyRequiredSavings = dailyRequired
                },
                Description = $"Create '{name}' savings goal for ${targetAmount:F2}" +
                             (parsedTargetDate.HasValue ? $" by {parsedTargetDate.Value:MMM d, yyyy}" : "") +
                             (allocationPercentage > 0 ? $" (auto-allocate {allocationPercentage}% of budget leftover)" : ""),
                UIOptions = new
                {
                    ShowDatePicker = true,
                    ShowAllocationSlider = true,
                    SuggestedIcons = new[] { "target", "piggy-bank", "vacation", "car", "home", "gift" }
                }
            };

            if (!confirmed)
            {
                return JsonSerializer.Serialize(confirmation, new JsonSerializerOptions { WriteIndented = true });
            }

            // Execute the creation
            var request = new CreateSavingsGoalRequest
            {
                Name = name,
                TargetAmount = targetAmount,
                TargetDate = parsedTargetDate,
                Description = description ?? $"Saving for {name}",
                AllocationPercentage = allocationPercentage,
                Type = SavingsGoalType.Personal,
                IconName = "target",
                ColorTheme = "primary"
            };

            var goal = await _savingsService.CreateSavingsGoalAsync(userId, request);

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Created '{name}' savings goal! Target: ${targetAmount:F2}" +
                         (parsedTargetDate.HasValue ? $" by {parsedTargetDate.Value:MMM d, yyyy}" : ""),
                Goal = new
                {
                    goal.Id,
                    goal.Name,
                    goal.TargetAmount,
                    goal.CurrentAmount,
                    goal.TargetDate,
                    goal.AllocationPercentage,
                    goal.DailyRequiredSavings
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in create_savings_goal for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "add_to_savings_goal")]
    [Description("Add money to an existing savings goal manually.")]
    public async Task<string> AddToSavingsGoalAsync(
        [Description("User ID making the allocation")] int userId,
        [Description("Savings goal ID to add to")] int goalId,
        [Description("Amount to add in dollars")] decimal amount,
        [Description("Set to true to execute the action")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: add_to_savings_goal called for user {UserId}, goalId={GoalId}, amount={Amount}, confirmed={Confirmed}", 
                userId, goalId, amount, confirmed);

            var goal = await _savingsService.GetSavingsGoalAsync(goalId);
            if (goal == null)
            {
                return JsonSerializer.Serialize(new { Error = "Savings goal not found." });
            }

            // Verify ownership
            if (goal.UserId != userId)
            {
                return JsonSerializer.Serialize(new { Error = "You don't have permission to modify this goal." });
            }

            var newTotal = goal.CurrentAmount + amount;
            var willComplete = newTotal >= goal.TargetAmount;

            var confirmation = new
            {
                RequiresConfirmation = !confirmed,
                ActionType = "add_to_savings_goal",
                Preview = new
                {
                    GoalName = goal.Name,
                    CurrentAmount = goal.CurrentAmount,
                    AddingAmount = amount,
                    NewTotal = newTotal,
                    TargetAmount = goal.TargetAmount,
                    ProgressAfter = goal.TargetAmount > 0 ? Math.Round((newTotal / goal.TargetAmount) * 100, 1) : 100,
                    WillComplete = willComplete
                },
                Description = $"Add ${amount:F2} to '{goal.Name}'" +
                             (willComplete ? " 🎉 This will complete the goal!" : $" (${goal.TargetAmount - newTotal:F2} remaining after)")
            };

            if (!confirmed)
            {
                return JsonSerializer.Serialize(confirmation, new JsonSerializerOptions { WriteIndented = true });
            }

            // Execute the allocation
            var request = new ManualAllocationRequest { Amount = amount };
            var updatedGoal = await _savingsService.AllocateToGoalAsync(goalId, request);

            var message = willComplete 
                ? $"🎉 Congratulations! You've completed your '{updatedGoal.Name}' savings goal!"
                : $"Added ${amount:F2} to '{updatedGoal.Name}'! Now at ${updatedGoal.CurrentAmount:F2} of ${updatedGoal.TargetAmount:F2}";

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = message,
                Goal = new
                {
                    updatedGoal.Id,
                    updatedGoal.Name,
                    updatedGoal.CurrentAmount,
                    updatedGoal.TargetAmount,
                    updatedGoal.ProgressPercentage,
                    updatedGoal.IsCompleted
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in add_to_savings_goal for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "update_savings_goal")]
    [Description("Update an existing savings goal's target amount, date, or other settings.")]
    public async Task<string> UpdateSavingsGoalAsync(
        [Description("User ID updating the goal")] int userId,
        [Description("Savings goal ID to update")] int goalId,
        [Description("New target amount (leave null to keep current)")] decimal? newTargetAmount = null,
        [Description("New target date (format: YYYY-MM-DD, leave null to keep current)")] string? newTargetDate = null,
        [Description("New allocation percentage (leave null to keep current)")] decimal? newAllocationPercentage = null,
        [Description("Set to true to execute the action")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: update_savings_goal called for user {UserId}, goalId={GoalId}", userId, goalId);

            var goal = await _savingsService.GetSavingsGoalAsync(goalId);
            if (goal == null)
            {
                return JsonSerializer.Serialize(new { Error = "Savings goal not found." });
            }

            if (goal.UserId != userId)
            {
                return JsonSerializer.Serialize(new { Error = "You don't have permission to modify this goal." });
            }

            DateTime? parsedDate = null;
            if (!string.IsNullOrEmpty(newTargetDate))
            {
                if (!DateTime.TryParse(newTargetDate, out var date))
                {
                    return JsonSerializer.Serialize(new { Error = $"Invalid date format: {newTargetDate}. Please use YYYY-MM-DD format." });
                }
                parsedDate = date;
            }

            var changes = new List<string>();
            if (newTargetAmount.HasValue && newTargetAmount.Value != goal.TargetAmount)
                changes.Add($"Target: ${goal.TargetAmount:F2} → ${newTargetAmount.Value:F2}");
            if (parsedDate.HasValue && parsedDate != goal.TargetDate)
                changes.Add($"Date: {goal.TargetDate?.ToString("MMM d, yyyy") ?? "none"} → {parsedDate.Value:MMM d, yyyy}");
            if (newAllocationPercentage.HasValue && newAllocationPercentage.Value != goal.AllocationPercentage)
                changes.Add($"Auto-allocation: {goal.AllocationPercentage}% → {newAllocationPercentage.Value}%");

            if (!changes.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    NoChanges = true,
                    Message = "No changes specified.",
                    CurrentGoal = new
                    {
                        goal.Name,
                        goal.TargetAmount,
                        goal.TargetDate,
                        goal.AllocationPercentage
                    }
                });
            }

            var confirmation = new
            {
                RequiresConfirmation = !confirmed,
                ActionType = "update_savings_goal",
                Preview = new
                {
                    GoalName = goal.Name,
                    Changes = changes
                },
                Description = $"Update '{goal.Name}': {string.Join(", ", changes)}"
            };

            if (!confirmed)
            {
                return JsonSerializer.Serialize(confirmation, new JsonSerializerOptions { WriteIndented = true });
            }

            var request = new UpdateSavingsGoalRequest
            {
                Name = goal.Name,
                Description = goal.Description,
                TargetAmount = newTargetAmount ?? goal.TargetAmount,
                TargetDate = parsedDate ?? goal.TargetDate,
                AllocationPercentage = newAllocationPercentage ?? goal.AllocationPercentage,
                IconName = goal.IconName,
                ColorTheme = goal.ColorTheme,
                IsActive = goal.IsActive
            };

            var updatedGoal = await _savingsService.UpdateSavingsGoalAsync(goalId, request);

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Updated '{updatedGoal.Name}'!",
                Goal = new
                {
                    updatedGoal.Id,
                    updatedGoal.Name,
                    updatedGoal.TargetAmount,
                    updatedGoal.TargetDate,
                    updatedGoal.AllocationPercentage,
                    updatedGoal.CurrentAmount,
                    updatedGoal.ProgressPercentage
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in update_savings_goal for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }
}
