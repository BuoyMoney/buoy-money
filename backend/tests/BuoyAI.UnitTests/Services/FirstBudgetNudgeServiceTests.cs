using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class FirstBudgetNudgeServiceTests
{
  private readonly Mock<IBudgetService> _budgets = new();
  private readonly Mock<INotificationService> _notifications = new();
  private readonly Mock<INotificationRepository> _notificationRepo = new();
  private readonly FirstBudgetNudgeService _service;

  public FirstBudgetNudgeServiceTests()
  {
    _service = new FirstBudgetNudgeService(
      _budgets.Object, _notifications.Object, _notificationRepo.Object,
      new Mock<ILogger<FirstBudgetNudgeService>>().Object);
  }

  private void NoBudgets() =>
    _budgets.Setup(b => b.GetBudgetsAsync(7)).ReturnsAsync(new List<BudgetDto>());

  private void NotYetNudged() =>
    _notificationRepo.Setup(n => n.GetNotificationCountByTypeAsync(
        7, FirstBudgetNudgeService.NotificationType, It.IsAny<DateTime>()))
      .ReturnsAsync(0);

  [Fact]
  public async Task NudgeIfNoBudget_NoBudgetAndNeverNudged_SendsOnce()
  {
    NoBudgets();
    NotYetNudged();

    var sent = await _service.NudgeIfNoBudgetAsync(7);

    sent.Should().BeTrue();
    _notifications.Verify(n => n.CreateNotificationAsync(7, It.Is<CreateNotificationRequest>(r =>
      r.Type == FirstBudgetNudgeService.NotificationType
      && r.ActionUrl == FirstBudgetNudgeService.BudgetsRoute
      && r.Title.Contains("transactions"))), Times.Once);
  }

  [Fact]
  public async Task NudgeIfNoBudget_UserHasBudget_DoesNothing()
  {
    _budgets.Setup(b => b.GetBudgetsAsync(7)).ReturnsAsync(new List<BudgetDto> { new() });

    var sent = await _service.NudgeIfNoBudgetAsync(7);

    sent.Should().BeFalse();
    _notifications.Verify(n => n.CreateNotificationAsync(It.IsAny<int>(), It.IsAny<CreateNotificationRequest>()), Times.Never);
  }

  [Fact]
  public async Task NudgeIfNoBudget_AlreadyNudged_DoesNotRepeat()
  {
    NoBudgets();
    _notificationRepo.Setup(n => n.GetNotificationCountByTypeAsync(
        7, FirstBudgetNudgeService.NotificationType, It.IsAny<DateTime>()))
      .ReturnsAsync(1);

    var sent = await _service.NudgeIfNoBudgetAsync(7);

    sent.Should().BeFalse();
    _notifications.Verify(n => n.CreateNotificationAsync(It.IsAny<int>(), It.IsAny<CreateNotificationRequest>()), Times.Never);
  }

  [Fact]
  public async Task NudgeIfNoBudget_NotificationFails_SwallowsAndReturnsFalse()
  {
    NoBudgets();
    NotYetNudged();
    _notifications.Setup(n => n.CreateNotificationAsync(7, It.IsAny<CreateNotificationRequest>()))
      .ThrowsAsync(new InvalidOperationException("push down"));

    var act = async () => await _service.NudgeIfNoBudgetAsync(7);

    (await act.Should().NotThrowAsync()).Subject.Should().BeFalse();
  }
}
