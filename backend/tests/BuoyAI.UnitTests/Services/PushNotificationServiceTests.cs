using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class PushNotificationServiceTests
{
  private readonly Mock<IFcmSender> _fcmSender = new();
  private readonly Mock<ILogger<PushNotificationService>> _logger = new();
  private readonly BuoyAIDbContext _context;
  private readonly PushNotificationService _service;

  public PushNotificationServiceTests()
  {
    _context = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    _fcmSender.Setup(f => f.IsEnabled).Returns(true);
    _fcmSender
      .Setup(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
        It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(new FcmSendResult(1, 0, Array.Empty<string>()));
    _service = new PushNotificationService(_context, _fcmSender.Object, _logger.Object);
  }

  private void AddDevice(int userId, string token = "token-1") =>
    _context.UserDeviceTokens.Add(new UserDeviceToken { UserId = userId, Token = token, Platform = "android" });

  // ===== SendToUserAsync gating =====

  [Fact]
  public async Task SendToUserAsync_HappyPath_SendsToRegisteredDevices()
  {
    AddDevice(1);
    await _context.SaveChangesAsync();

    await _service.SendToUserAsync(1, "budget_alert", "title", "body", "/budgets");

    _fcmSender.Verify(f => f.SendAsync(
      It.Is<IReadOnlyList<string>>(t => t.Single() == "token-1"),
      "title", "body",
      It.Is<IReadOnlyDictionary<string, string>>(d => d["route"] == "/budgets" && d["type"] == "budget_alert"),
      It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task SendToUserAsync_NoActionUrl_FallsBackToNotificationsRoute()
  {
    AddDevice(1);
    await _context.SaveChangesAsync();

    await _service.SendToUserAsync(1, "budget_alert", "title", "body", null);

    _fcmSender.Verify(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
      It.Is<IReadOnlyDictionary<string, string>>(d => d["route"] == "/notifications"),
      It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task SendToUserAsync_FcmDisabled_DoesNothing()
  {
    _fcmSender.Setup(f => f.IsEnabled).Returns(false);
    AddDevice(1);
    await _context.SaveChangesAsync();

    await _service.SendToUserAsync(1, "budget_alert", "title", "body", null);

    _fcmSender.Verify(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
      It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task SendToUserAsync_MasterToggleOff_DoesNotSend()
  {
    AddDevice(1);
    _context.UserPushPreferences.Add(new UserPushPreferences { UserId = 1, PushEnabled = false });
    await _context.SaveChangesAsync();

    await _service.SendToUserAsync(1, "budget_alert", "title", "body", null);

    _fcmSender.Verify(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
      It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task SendToUserAsync_CategoryToggleOff_DoesNotSend()
  {
    AddDevice(1);
    _context.UserPushPreferences.Add(new UserPushPreferences { UserId = 1, BudgetAlerts = false });
    await _context.SaveChangesAsync();

    await _service.SendToUserAsync(1, "budget_alert", "title", "body", null);

    _fcmSender.Verify(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
      It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task SendToUserAsync_UnmappedType_SendsWhenMasterEnabled()
  {
    AddDevice(1);
    // Every category off — an unmapped type is gated by the master toggle only.
    _context.UserPushPreferences.Add(new UserPushPreferences
    {
      UserId = 1,
      ConnectionIssues = false,
      BudgetAlerts = false,
      BillReminders = false,
      SubscriptionDetection = false,
      TransactionReview = false
    });
    await _context.SaveChangesAsync();

    await _service.SendToUserAsync(1, "some_future_type", "title", "body", null);

    _fcmSender.Verify(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
      It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task SendToUserAsync_NoDevices_DoesNotSend()
  {
    await _service.SendToUserAsync(1, "budget_alert", "title", "body", null);

    _fcmSender.Verify(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
      It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task SendToUserAsync_FcmThrows_DoesNotPropagate()
  {
    AddDevice(1);
    await _context.SaveChangesAsync();
    _fcmSender
      .Setup(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
        It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new InvalidOperationException("FCM down"));

    var act = () => _service.SendToUserAsync(1, "budget_alert", "title", "body", null);

    await act.Should().NotThrowAsync();
  }

  [Fact]
  public async Task SendToUserAsync_InvalidTokensReported_ArePruned()
  {
    AddDevice(1, "good-token");
    AddDevice(1, "dead-token");
    await _context.SaveChangesAsync();
    _fcmSender
      .Setup(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
        It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(new FcmSendResult(1, 1, new[] { "dead-token" }));

    await _service.SendToUserAsync(1, "budget_alert", "title", "body", null);

    var remaining = await _context.UserDeviceTokens.Select(t => t.Token).ToListAsync();
    remaining.Should().Equal("good-token");
  }

  // ===== Quiet hours =====

  [Fact]
  public async Task SendToUserAsync_DuringQuietHours_QueuesInsteadOfSending()
  {
    AddDevice(1);
    _context.UserPushPreferences.Add(new UserPushPreferences
    {
      UserId = 1,
      QuietHoursEnabled = true,
      QuietHoursStartMinutes = 0,
      QuietHoursEndMinutes = 1439, // Nearly all day — the current instant is inside the window.
      TimeZoneOffsetMinutes = 0
    });
    await _context.SaveChangesAsync();

    await _service.SendToUserAsync(1, "budget_alert", "title", "body", "/budgets");

    _fcmSender.Verify(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
      It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    var queued = await _context.QueuedPushNotifications.SingleAsync();
    queued.UserId.Should().Be(1);
    queued.Title.Should().Be("title");
    queued.DeliverAfter.Should().BeAfter(DateTime.UtcNow);
  }

  [Theory]
  // Window 22:00 → 07:00 (wraps midnight), UTC user.
  [InlineData(23 * 60, true)]   // 23:00 — inside
  [InlineData(3 * 60, true)]    // 03:00 — inside
  [InlineData(12 * 60, false)]  // noon — outside
  [InlineData(21 * 60 + 59, false)] // 21:59 — just before start
  [InlineData(7 * 60, false)]   // 07:00 — the end minute is outside (exclusive)
  public void IsInQuietHours_WrappingWindow_ClassifiesCorrectly(int localMinutes, bool expected)
  {
    var prefs = new UserPushPreferences
    {
      QuietHoursEnabled = true,
      QuietHoursStartMinutes = 22 * 60,
      QuietHoursEndMinutes = 7 * 60,
      TimeZoneOffsetMinutes = 0
    };
    var utc = DateTime.UtcNow.Date.AddMinutes(localMinutes);

    PushNotificationService.IsInQuietHours(prefs, utc).Should().Be(expected);
  }

  [Fact]
  public void IsInQuietHours_RespectsTimeZoneOffset()
  {
    // 02:00 UTC = 21:00 local for UTC-5 — outside a 22:00→07:00 window.
    var prefs = new UserPushPreferences
    {
      QuietHoursEnabled = true,
      QuietHoursStartMinutes = 22 * 60,
      QuietHoursEndMinutes = 7 * 60,
      TimeZoneOffsetMinutes = -300
    };
    var utc = DateTime.UtcNow.Date.AddHours(2);

    PushNotificationService.IsInQuietHours(prefs, utc).Should().BeFalse();
    // …but 04:00 UTC = 23:00 local is inside.
    PushNotificationService.IsInQuietHours(prefs, utc.AddHours(2)).Should().BeTrue();
  }

  [Fact]
  public void NextQuietHoursEnd_ReturnsTheComingEndInstant()
  {
    var prefs = new UserPushPreferences
    {
      QuietHoursEnabled = true,
      QuietHoursStartMinutes = 22 * 60,
      QuietHoursEndMinutes = 7 * 60,
      TimeZoneOffsetMinutes = 0
    };
    // 23:00 local → quiet ends 07:00 next day = 8h away.
    var utc = DateTime.UtcNow.Date.AddHours(23);

    var end = PushNotificationService.NextQuietHoursEnd(prefs, utc);

    end.Should().Be(utc.AddHours(8));
  }

  // ===== Queue delivery =====

  [Fact]
  public async Task DeliverDueQueuedAsync_SendsDueAndRemovesRow()
  {
    AddDevice(1);
    _context.QueuedPushNotifications.Add(new QueuedPushNotification
    {
      UserId = 1,
      Title = "queued",
      Body = "body",
      DataJson = "{\"route\":\"/budgets\",\"type\":\"budget_alert\"}",
      DeliverAfter = DateTime.UtcNow.AddMinutes(-1)
    });
    await _context.SaveChangesAsync();

    var sent = await _service.DeliverDueQueuedAsync();

    sent.Should().Be(1);
    (await _context.QueuedPushNotifications.CountAsync()).Should().Be(0);
    _fcmSender.Verify(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), "queued", "body",
      It.Is<IReadOnlyDictionary<string, string>>(d => d["route"] == "/budgets"),
      It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task DeliverDueQueuedAsync_NotYetDue_LeavesRowAlone()
  {
    AddDevice(1);
    _context.QueuedPushNotifications.Add(new QueuedPushNotification
    {
      UserId = 1,
      Title = "queued",
      Body = "body",
      DataJson = "{}",
      DeliverAfter = DateTime.UtcNow.AddHours(2)
    });
    await _context.SaveChangesAsync();

    var sent = await _service.DeliverDueQueuedAsync();

    sent.Should().Be(0);
    (await _context.QueuedPushNotifications.CountAsync()).Should().Be(1);
  }

  [Fact]
  public async Task DeliverDueQueuedAsync_PushDisabledMeanwhile_DiscardsWithoutSending()
  {
    AddDevice(1);
    _context.UserPushPreferences.Add(new UserPushPreferences { UserId = 1, PushEnabled = false });
    _context.QueuedPushNotifications.Add(new QueuedPushNotification
    {
      UserId = 1,
      Title = "queued",
      Body = "body",
      DataJson = "{}",
      DeliverAfter = DateTime.UtcNow.AddMinutes(-1)
    });
    await _context.SaveChangesAsync();

    var sent = await _service.DeliverDueQueuedAsync();

    sent.Should().Be(0);
    (await _context.QueuedPushNotifications.CountAsync()).Should().Be(0);
    _fcmSender.Verify(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
      It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  // ===== Device registration =====

  [Fact]
  public async Task RegisterDeviceAsync_NewToken_CreatesRow()
  {
    await _service.RegisterDeviceAsync(1, new RegisterDeviceRequest
    {
      Token = "t1",
      Platform = "Android",
      TimeZoneOffsetMinutes = -300
    });

    var device = await _context.UserDeviceTokens.SingleAsync();
    device.UserId.Should().Be(1);
    device.Platform.Should().Be("android");
    (await _context.UserPushPreferences.SingleAsync()).TimeZoneOffsetMinutes.Should().Be(-300);
  }

  [Fact]
  public async Task RegisterDeviceAsync_CalledTwice_KeepsOnePreferencesRow()
  {
    // The app registers on login AND on FCM token refresh, which fire near-simultaneously
    // on first launch. Registration must be idempotent on BOTH rows it writes, not just
    // the device token: a duplicate insert on ux_user_push_preferences_user_id 500'd the
    // whole call in production on 1.5.13.
    //
    // NOTE: EF InMemory does not enforce unique indexes, so this asserts idempotency
    // rather than reproducing the 23505 itself. The real race only surfaces on Postgres.
    var request = new RegisterDeviceRequest
    {
      Token = "t1",
      Platform = "Android",
      TimeZoneOffsetMinutes = -300
    };

    await _service.RegisterDeviceAsync(1, request);
    await _service.RegisterDeviceAsync(1, request);

    (await _context.UserPushPreferences.CountAsync()).Should().Be(1);
    (await _context.UserDeviceTokens.CountAsync()).Should().Be(1);
  }

  [Fact]
  public async Task RegisterDeviceAsync_PreferencesAlreadyExist_UpdatesRatherThanInserting()
  {
    // The losing side of the race arrives to find the row already there. It must update
    // in place; inserting again is exactly what violated the unique index.
    _context.UserPushPreferences.Add(new UserPushPreferences { UserId = 1, TimeZoneOffsetMinutes = 0 });
    await _context.SaveChangesAsync();

    await _service.RegisterDeviceAsync(1, new RegisterDeviceRequest
    {
      Token = "t1",
      Platform = "Android",
      TimeZoneOffsetMinutes = -300
    });

    var prefs = await _context.UserPushPreferences.SingleAsync();
    prefs.TimeZoneOffsetMinutes.Should().Be(-300);
  }

  [Fact]
  public async Task RegisterDeviceAsync_ExistingTokenOtherUser_ReparentsToNewUser()
  {
    AddDevice(1, "shared-device");
    await _context.SaveChangesAsync();

    await _service.RegisterDeviceAsync(2, new RegisterDeviceRequest { Token = "shared-device", Platform = "ios" });

    var device = await _context.UserDeviceTokens.SingleAsync();
    device.UserId.Should().Be(2);
    device.Platform.Should().Be("ios");
  }

  [Fact]
  public async Task RegisterDeviceAsync_SameTokenTwice_IsIdempotent_OneRow()
  {
    // The app registers on login AND on FCM token refresh; back-to-back calls with the
    // same token must converge to a single row, not duplicate/500 (prod race, v1.5.7).
    var req = new RegisterDeviceRequest { Token = "t1", Platform = "android" };

    await _service.RegisterDeviceAsync(1, req);
    await _service.RegisterDeviceAsync(1, req);

    var rows = await _context.UserDeviceTokens.Where(t => t.Token == "t1").ToListAsync();
    rows.Should().HaveCount(1);
    rows[0].UserId.Should().Be(1);
  }

  [Fact]
  public async Task UnregisterDeviceAsync_OwnedToken_RemovesIt()
  {
    AddDevice(1);
    await _context.SaveChangesAsync();

    var removed = await _service.UnregisterDeviceAsync(1, "token-1");

    removed.Should().BeTrue();
    (await _context.UserDeviceTokens.CountAsync()).Should().Be(0);
  }

  [Fact]
  public async Task UnregisterDeviceAsync_SomeoneElsesToken_ReturnsFalseAndKeepsIt()
  {
    AddDevice(1);
    await _context.SaveChangesAsync();

    var removed = await _service.UnregisterDeviceAsync(99, "token-1");

    removed.Should().BeFalse();
    (await _context.UserDeviceTokens.CountAsync()).Should().Be(1);
  }

  // ===== Preferences =====

  [Fact]
  public async Task GetPreferencesAsync_NoRow_ReturnsDefaults()
  {
    var dto = await _service.GetPreferencesAsync(1);

    dto.PushEnabled.Should().BeTrue();
    dto.BudgetAlerts.Should().BeTrue();
    dto.QuietHoursEnabled.Should().BeFalse();
    dto.RegisteredDeviceCount.Should().Be(0);
  }

  [Fact]
  public async Task UpdatePreferencesAsync_CreatesRowAndRoundTrips()
  {
    AddDevice(1);
    await _context.SaveChangesAsync();

    var dto = await _service.UpdatePreferencesAsync(1, new UpdatePushPreferencesRequest
    {
      PushEnabled = true,
      BudgetAlerts = false,
      QuietHoursEnabled = true,
      QuietHoursStartMinutes = 21 * 60,
      QuietHoursEndMinutes = 6 * 60,
      TimeZoneOffsetMinutes = 60
    });

    dto.BudgetAlerts.Should().BeFalse();
    dto.QuietHoursStartMinutes.Should().Be(21 * 60);
    dto.RegisteredDeviceCount.Should().Be(1);
    var row = await _context.UserPushPreferences.SingleAsync();
    row.TimeZoneOffsetMinutes.Should().Be(60);
  }

  // ===== Stale-token pruning =====

  [Fact]
  public async Task PruneStaleTokensAsync_RemovesOnlyTokensPast270Days()
  {
    _context.UserDeviceTokens.Add(new UserDeviceToken
    {
      UserId = 1, Token = "ancient", Platform = "android",
      LastSeenAt = DateTime.UtcNow.AddDays(-300)
    });
    AddDevice(1, "fresh");
    await _context.SaveChangesAsync();

    var pruned = await _service.PruneStaleTokensAsync();

    pruned.Should().Be(1);
    (await _context.UserDeviceTokens.Select(t => t.Token).ToListAsync()).Should().Equal("fresh");
  }
}
