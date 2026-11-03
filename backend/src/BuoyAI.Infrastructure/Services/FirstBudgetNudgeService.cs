using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The post-import activation nudge. A new user can connect a bank,
/// import hundreds of transactions, and left without ever creating a budget: nothing told them
/// what to do next. When a connection's historical import completes and the user still
/// has no budget, send one notification (in-app + push) pointing at budget creation.
/// Once per user, ever; a budget's existence is the only thing that matters, so users who
/// already have one never see it.
/// </summary>
public interface IFirstBudgetNudgeService
{
    /// <summary>Returns true when a nudge was sent.</summary>
    Task<bool> NudgeIfNoBudgetAsync(int userId, CancellationToken cancellationToken = default);
}

public class FirstBudgetNudgeService : IFirstBudgetNudgeService
{
    public const string NotificationType = "first_budget_nudge";
    public const string BudgetsRoute = "/budgets";

    private readonly IBudgetService _budgetService;
    private readonly INotificationService _notificationService;
    private readonly INotificationRepository _notificationRepository;
    private readonly ILogger<FirstBudgetNudgeService> _logger;

    public FirstBudgetNudgeService(
        IBudgetService budgetService,
        INotificationService notificationService,
        INotificationRepository notificationRepository,
        ILogger<FirstBudgetNudgeService> logger)
    {
        _budgetService = budgetService;
        _notificationService = notificationService;
        _notificationRepository = notificationRepository;
        _logger = logger;
    }

    public async Task<bool> NudgeIfNoBudgetAsync(int userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var budgets = await _budgetService.GetBudgetsAsync(userId);
            if (budgets.Any())
            {
                return false;
            }

            var alreadySent = await _notificationRepository.GetNotificationCountByTypeAsync(
                userId, NotificationType, DateTime.MinValue);
            if (alreadySent > 0)
            {
                return false;
            }

            await _notificationService.CreateNotificationAsync(userId, new CreateNotificationRequest
            {
                Type = NotificationType,
                Title = "Your transactions are in",
                Message = "Build your first budget in one tap and watch it start counting up.",
                ActionUrl = BudgetsRoute,
                ActionText = "Build my budget",
            });

            _logger.LogInformation("Sent first-budget nudge to user {UserId}", userId);
            return true;
        }
        catch (Exception ex)
        {
            // Activation nudges are a bonus channel: never let them break an import.
            _logger.LogWarning(ex, "First-budget nudge failed for user {UserId}", userId);
            return false;
        }
    }
}
