using System;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.UnitTests.Services;

public class BillReminderServiceTests
{
  private readonly Mock<INotificationService> _notificationService = new();
  private readonly Mock<INotificationRepository> _notificationRepository = new();
  private readonly BuoyAIDbContext _context;
  private readonly BillReminderService _service;

  public BillReminderServiceTests()
  {
    _context = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    var services = new ServiceCollection();
    services.AddSingleton(_context);
    services.AddSingleton(_notificationService.Object);
    services.AddSingleton(_notificationRepository.Object);
    var provider = services.BuildServiceProvider();

    _service = new BillReminderService(
      provider.GetRequiredService<IServiceScopeFactory>(),
      Mock.Of<ILogger<BillReminderService>>(),
      new BuoyAiMetrics(new BillReminderTestMeterFactory()));
  }

  private Subscription Sub(int userId, string name, decimal amount, int daysOut,
    SubscriptionStatus status = SubscriptionStatus.Active) => new()
  {
    UserId = userId,
    Name = name,
    Amount = amount,
    Status = status,
    NextDueDate = DateTime.UtcNow.Date.AddDays(daysOut).AddHours(3),
    Frequency = SubscriptionFrequency.Monthly
  };

  [Fact]
  public async Task RunRemindersAsync_BillDueInTwoDays_CreatesSingleBillNotification()
  {
    _context.Subscriptions.Add(Sub(1, "Netflix", 15.99m, daysOut: 2));
    await _context.SaveChangesAsync();

    await _service.RunRemindersAsync(CancellationToken.None);

    _notificationService.Verify(n => n.CreateNotificationAsync(1,
      It.Is<CreateNotificationRequest>(r =>
        r.Type == "bill_reminder" &&
        r.Title.Contains("Netflix") &&
        r.Message.Contains("$15.99") &&
        r.ActionUrl == "/subscriptions")), Times.Once);
  }

  [Fact]
  public async Task RunRemindersAsync_MultipleBillsSameUser_CreatesOneDigest()
  {
    _context.Subscriptions.Add(Sub(1, "Netflix", 15.99m, daysOut: 2));
    _context.Subscriptions.Add(Sub(1, "Spotify", 9.99m, daysOut: 2));
    await _context.SaveChangesAsync();

    await _service.RunRemindersAsync(CancellationToken.None);

    _notificationService.Verify(n => n.CreateNotificationAsync(1,
      It.Is<CreateNotificationRequest>(r =>
        r.Title.Contains("2 bills") &&
        r.Message.Contains("$25.98"))), Times.Once);
    _notificationService.Verify(n => n.CreateNotificationAsync(It.IsAny<int>(),
      It.IsAny<CreateNotificationRequest>()), Times.Once);
  }

  [Fact]
  public async Task RunRemindersAsync_BillOutsideWindow_NotReminded()
  {
    _context.Subscriptions.Add(Sub(1, "Tomorrow", 5m, daysOut: 1));
    _context.Subscriptions.Add(Sub(1, "NextWeek", 5m, daysOut: 7));
    await _context.SaveChangesAsync();

    await _service.RunRemindersAsync(CancellationToken.None);

    _notificationService.Verify(n => n.CreateNotificationAsync(It.IsAny<int>(),
      It.IsAny<CreateNotificationRequest>()), Times.Never);
  }

  [Fact]
  public async Task RunRemindersAsync_InactiveSubscription_NotReminded()
  {
    _context.Subscriptions.Add(Sub(1, "Cancelled", 5m, daysOut: 2, SubscriptionStatus.Cancelled));
    _context.Subscriptions.Add(Sub(1, "Paused", 5m, daysOut: 2, SubscriptionStatus.Paused));
    await _context.SaveChangesAsync();

    await _service.RunRemindersAsync(CancellationToken.None);

    _notificationService.Verify(n => n.CreateNotificationAsync(It.IsAny<int>(),
      It.IsAny<CreateNotificationRequest>()), Times.Never);
  }

  [Fact]
  public async Task RunRemindersAsync_AlreadyRemindedToday_Deduped()
  {
    _context.Subscriptions.Add(Sub(1, "Netflix", 15.99m, daysOut: 2));
    await _context.SaveChangesAsync();
    _notificationRepository
      .Setup(r => r.GetNotificationCountByTypeAsync(1, "bill_reminder", It.IsAny<DateTime>()))
      .ReturnsAsync(1);

    await _service.RunRemindersAsync(CancellationToken.None);

    _notificationService.Verify(n => n.CreateNotificationAsync(It.IsAny<int>(),
      It.IsAny<CreateNotificationRequest>()), Times.Never);
  }
}

internal class BillReminderTestMeterFactory : IMeterFactory
{
  public Meter Create(MeterOptions options) => new(options.Name, options.Version);
  public void Dispose() { }
}
