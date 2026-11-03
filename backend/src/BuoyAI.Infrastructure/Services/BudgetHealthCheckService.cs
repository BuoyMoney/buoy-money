using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Background service that runs daily to check for budget health issues:
/// - Overspending in categories (last 3 days of period)
/// - Under-income (received less than expected, last 3 days of period)
/// Creates notifications for users to resolve these issues
/// </summary>
public class BudgetHealthCheckService : BackgroundService
{
    private const string JobName = "BudgetHealthCheck";
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<BudgetHealthCheckService> _logger;
    private readonly BuoyAiMetrics _metrics;
    
    // Run daily at 8:00 AM UTC
    private readonly int _targetHour = 8;
    private readonly int _targetMinute = 0;

    public BudgetHealthCheckService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<BudgetHealthCheckService> logger,
        BuoyAiMetrics metrics)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Budget Health Check Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait until the next scheduled run time
                await WaitUntilNextScheduledTimeAsync(stoppingToken);
                
                if (stoppingToken.IsCancellationRequested)
                    break;

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                _metrics.RecordBackgroundJobRun(JobName);
                await RunHealthCheckAsync(stoppingToken);
                stopwatch.Stop();
                _metrics.RecordBackgroundJobSuccess(JobName);
                _metrics.RecordBackgroundJobDuration(JobName, stopwatch.Elapsed.TotalSeconds);

                // After running, wait at least 12 hours before checking schedule again
                // This prevents double-runs
                await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Budget Health Check Service cancelled");
                break;
            }
            catch (Exception ex)
            {
                _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
                _logger.LogError(ex, "Error in Budget Health Check Service");
                // Wait 1 hour before retrying on error
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        _logger.LogInformation("Budget Health Check Service stopped");
    }

    private async Task WaitUntilNextScheduledTimeAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        DateTime targetTime;
        
        // Calculate next run time (today or tomorrow at target hour)
        if (now.Hour < _targetHour || (now.Hour == _targetHour && now.Minute < _targetMinute))
        {
            // Run today
            targetTime = new DateTime(now.Year, now.Month, now.Day, _targetHour, _targetMinute, 0, DateTimeKind.Utc);
        }
        else
        {
            // Run tomorrow
            var tomorrow = now.AddDays(1);
            targetTime = new DateTime(tomorrow.Year, tomorrow.Month, tomorrow.Day, _targetHour, _targetMinute, 0, DateTimeKind.Utc);
        }

        var delay = targetTime - now;
        _logger.LogInformation("Budget Health Check scheduled to run at {TargetTime} (in {Delay})", targetTime, delay);

        await Task.Delay(delay, cancellationToken);
    }

    private async Task RunHealthCheckAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running daily budget health check");

        using var scope = _serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var spendingService = scope.ServiceProvider.GetRequiredService<IBudgetSpendingService>();

        var now = DateTime.UtcNow;
        var healthIssuesFound = 0;
        var usersNotified = 0;

        // Get all active budgets that are in the last 3 days of their period
        var budgets = await context.Budgets
            .Include(b => b.Categories)
              .ThenInclude(c => c.CategoryNav)
            .Include(b => b.User)
            .Where(b => b.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var budget in budgets)
        {
            // Check if we're in the last 3 days of the period
            // Use date-only comparison to avoid off-by-one errors from time components
            var daysUntilPeriodEnd = (budget.CurrentPeriodEnd.Date - now.Date).Days;
            if (daysUntilPeriodEnd > 3 || daysUntilPeriodEnd < 0)
            {
                continue; // Not in the critical window
            }

            var issuesForBudget = new List<BudgetHealthIssue>();

            // Get actual spending from transactions for this budget
            var categorySpending = await spendingService.GetCategorySpendingAsync(budget.Id);

            // Check each category for issues
            foreach (var category in budget.Categories)
            {
                var spentAmount = categorySpending.GetValueOrDefault(category.Id, 0);
                
                // Skip income categories for overspending check
                if (category.IsIncome)
                {
                    // For income: check if received less than expected
                    var incomeDeficit = category.AllocatedAmount - spentAmount;
                    if (incomeDeficit > 0 && category.AllocatedAmount > 0)
                    {
                        issuesForBudget.Add(new BudgetHealthIssue
                        {
                            Type = BudgetHealthIssueType.UnderIncome,
                            CategoryId = category.Id,
                            CategoryName = category.CategoryNav?.Name ?? "Unknown",
                            ExpectedAmount = category.AllocatedAmount,
                            ActualAmount = spentAmount,
                            Difference = incomeDeficit
                        });
                    }
                }
                else
                {
                    // For expenses: check if overspending
                    var available = category.AllocatedAmount + category.RolloverBalance;
                    var overspend = spentAmount - available;
                    if (overspend > 0)
                    {
                        issuesForBudget.Add(new BudgetHealthIssue
                        {
                            Type = BudgetHealthIssueType.Overspending,
                            CategoryId = category.Id,
                            CategoryName = category.CategoryNav?.Name ?? "Unknown",
                            ExpectedAmount = available,
                            ActualAmount = spentAmount,
                            Difference = overspend
                        });
                    }
                }
            }

            if (issuesForBudget.Count > 0)
            {
                healthIssuesFound += issuesForBudget.Count;

                // Create notification for the user
                await CreateHealthCheckNotificationAsync(context, budget.UserId, budget, issuesForBudget);
                usersNotified++;
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Budget health check completed. Found {IssueCount} issues across {UserCount} users",
            healthIssuesFound, usersNotified);
        
        if (healthIssuesFound > 0)
        {
            _metrics.RecordUnhealthyBudgets(healthIssuesFound);
        }
    }

    private async Task CreateHealthCheckNotificationAsync(
        BuoyAIDbContext context, 
        int userId, 
        Budget budget,
        List<BudgetHealthIssue> issues)
    {
        // Build notification message
        var overspendingIssues = issues.Where(i => i.Type == BudgetHealthIssueType.Overspending).ToList();
        var underIncomeIssues = issues.Where(i => i.Type == BudgetHealthIssueType.UnderIncome).ToList();

        var title = "Budget Check-In Required";
        var message = new System.Text.StringBuilder();
        // Use date-only comparison to avoid off-by-one errors from time components
        var daysRemaining = (budget.CurrentPeriodEnd.Date - DateTime.UtcNow.Date).Days;
        message.AppendLine($"Your budget \"{budget.Name}\" ends in {daysRemaining} day{(daysRemaining == 1 ? "" : "s")}.");
        
        if (overspendingIssues.Any())
        {
            var totalOverspend = overspendingIssues.Sum(i => i.Difference);
            message.AppendLine($"\n📊 {overspendingIssues.Count} categories are over budget (total: ${totalOverspend:N2})");
            foreach (var issue in overspendingIssues.Take(3))
            {
                message.AppendLine($"  • {issue.CategoryName}: ${issue.Difference:N2} over");
            }
            if (overspendingIssues.Count > 3)
            {
                message.AppendLine($"  • ...and {overspendingIssues.Count - 3} more");
            }
        }

        if (underIncomeIssues.Any())
        {
            var totalUnderIncome = underIncomeIssues.Sum(i => i.Difference);
            message.AppendLine($"\n💰 {underIncomeIssues.Count} income sources below expected (total: ${totalUnderIncome:N2})");
            foreach (var issue in underIncomeIssues.Take(2))
            {
                message.AppendLine($"  • {issue.CategoryName}: ${issue.Difference:N2} under expected");
            }
        }

        message.AppendLine("\nReview your budget to resolve these issues before the period ends.");

        // Check if we already sent a similar notification recently
        var recentNotification = await context.Notifications
            .Where(n => n.UserId == userId 
                && n.Type == "budget_alert"
                && n.CreatedAt > DateTime.UtcNow.AddHours(-20)) // Within last 20 hours
            .FirstOrDefaultAsync();

        if (recentNotification != null)
        {
            // Don't spam the user, skip this notification
            _logger.LogDebug("Skipping duplicate budget health notification for user {UserId}", userId);
            return;
        }

        var notification = new Notification
        {
            UserId = userId,
            Type = "budget_alert",
            Title = title,
            Message = message.ToString(),
            // The app has no per-budget route; /budget/{id} landed on a missing page.
            ActionUrl = "/budgets",
            CreatedAt = DateTime.UtcNow,
            IsRead = false
        };

        context.Notifications.Add(notification);

        _logger.LogInformation(
            "Created budget health notification for user {UserId}: {IssueCount} issues found",
            userId, issues.Count);
    }
}

internal class BudgetHealthIssue
{
    public BudgetHealthIssueType Type { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public decimal ExpectedAmount { get; set; }
    public decimal ActualAmount { get; set; }
    public decimal Difference { get; set; }
}

internal enum BudgetHealthIssueType
{
    Overspending,
    UnderIncome
}
