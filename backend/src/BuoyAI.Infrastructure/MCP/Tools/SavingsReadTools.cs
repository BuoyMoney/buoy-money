using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tools for reading savings goals data.
/// </summary>
[McpServerToolType]
public sealed class SavingsReadTools
{
    private readonly ISavingsService _savingsService;
    private readonly ILogger<SavingsReadTools> _logger;

    public SavingsReadTools(
        ISavingsService savingsService,
        ILogger<SavingsReadTools> logger)
    {
        _savingsService = savingsService;
        _logger = logger;
    }

    [McpServerTool(Name = "get_savings_goals", ReadOnly = true)]
    [Description("Get all savings goals with their progress toward target amounts. Use this to understand the user's savings objectives and progress.")]
    public async Task<string> GetSavingsGoalsAsync(
        [Description("User ID to get savings goals for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_savings_goals called for user {UserId}", userId);
            
            var goals = await _savingsService.GetSavingsGoalsAsync(userId);
            var goalList = goals.ToList();

            if (!goalList.Any())
            {
                return JsonSerializer.Serialize(new
                {
                    Message = "No savings goals found. The user may want to create savings goals to track progress toward financial objectives.",
                    TotalGoals = 0
                });
            }

            var result = new
            {
                TotalGoals = goalList.Count,
                TotalSaved = goalList.Sum(g => g.CurrentAmount),
                TotalTarget = goalList.Sum(g => g.TargetAmount),
                Goals = goalList.Select(g => new
                {
                    g.Id,
                    g.Name,
                    g.Description,
                    g.CurrentAmount,
                    g.TargetAmount,
                    g.RemainingAmount,
                    PercentComplete = g.ProgressPercentage,
                    TargetDate = g.TargetDate?.ToString("yyyy-MM-dd"),
                    g.DaysRemaining,
                    g.IsCompleted,
                    g.IsActive,
                    g.ProgressStatus,
                    g.IsOnTrack,
                    g.DailyRequiredSavings
                })
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_savings_goals for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_savings_overview", ReadOnly = true)]
    [Description("Get an overview of the user's savings including total saved, recent achievements, and progress metrics.")]
    public async Task<string> GetSavingsOverviewAsync(
        [Description("User ID to get savings overview for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_savings_overview called for user {UserId}", userId);
            
            var overview = await _savingsService.GetSavingsOverviewAsync(userId);
            var achievements = await _savingsService.GetUserAchievementsAsync(userId);

            // Calculate total targeted from goals
            var totalTargeted = overview.Goals?.Sum(g => g.TargetAmount) ?? 0;
            
            var result = new
            {
                Summary = new
                {
                    overview.TotalSaved,
                    TotalTargeted = totalTargeted,
                    OverallProgress = totalTargeted > 0 
                        ? Math.Round((overview.TotalSaved / totalTargeted) * 100, 1) 
                        : 0,
                    overview.ActiveGoalsCount,
                    overview.CompletedGoalsCount,
                    overview.MonthlyGrowth,
                    overview.TotalAllocatedPerPeriod,
                    overview.UnallocatedAmount
                },
                RecentAchievements = achievements.Take(5).Select(a => new
                {
                    a.Title,
                    a.Description,
                    EarnedDate = a.EarnedDate.ToString("yyyy-MM-dd")
                }),
                // Find nearest goal to completion
                NearestGoal = overview.Goals?.Where(g => g.IsActive && !g.IsCompleted)
                    .OrderByDescending(g => g.ProgressPercentage)
                    .FirstOrDefault() is { } nearestGoal ? new
                {
                    nearestGoal.Name,
                    nearestGoal.CurrentAmount,
                    nearestGoal.TargetAmount,
                    nearestGoal.RemainingAmount,
                    nearestGoal.ProgressPercentage
                } : null
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_savings_overview for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }
}
