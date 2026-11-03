using System;
using System.Collections.Generic;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class FcmSenderMessageTests
{
  private static FirebaseAdmin.Messaging.MulticastMessage Build() =>
    FcmSender.BuildMessage(
      new[] { "token-a", "token-b" },
      "Achievement unlocked!",
      "Self-Aware",
      new Dictionary<string, string> { ["type"] = "gamification", ["route"] = "/notifications" });

  [Fact]
  public void BuildMessage_SetsEventTimestampToNow()
  {
    // Regression: AndroidNotification.EventTimestamp is a non-nullable DateTime, so
    // NOT setting it means DateTime.MinValue rather than "omitted". The SDK serialises
    // that into the payload, Android labels and sorts the notification by it, and every
    // push we sent showed up as ~2000 years old and sank to the bottom of the shade.
    var ts = Build().Android.Notification.EventTimestamp;

    ts.Should().NotBe(default);
    ts.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
  }

  [Fact]
  public void BuildMessage_CarriesTitleBodyChannelAndData()
  {
    var m = Build();

    m.Notification.Title.Should().Be("Achievement unlocked!");
    m.Notification.Body.Should().Be("Self-Aware");
    m.Android.Notification.ChannelId.Should().Be("buoyai_alerts");
    m.Data.Should().ContainKey("route").WhoseValue.Should().Be("/notifications");
    m.Tokens.Should().HaveCount(2);
  }
}
