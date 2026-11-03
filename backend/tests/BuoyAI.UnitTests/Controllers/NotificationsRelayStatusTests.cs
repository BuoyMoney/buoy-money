using BuoyAI.API.Controllers;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

/// <summary>GET /api/notifications/relay-status: what the owner's checklist reads before it calls notifications done.</summary>
public class NotificationsRelayStatusTests
{
  private static NotificationsController Controller(Edition edition)
  {
    var controller = new NotificationsController(Mock.Of<INotificationService>(), Mock.Of<IPushNotificationService>(),
      NullLogger<NotificationsController>.Instance);
    var services = new ServiceCollection().AddSingleton(new EditionInfo(edition)).BuildServiceProvider();
    controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
    return controller;
  }

  private static Mock<IPushRelaySender> Relay(bool enabled, bool ready)
  {
    var relay = new Mock<IPushRelaySender>();
    relay.SetupGet(r => r.IsEnabled).Returns(enabled);
    relay.Setup(r => r.IsReadyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ready);
    return relay;
  }

  [Fact]
  public async Task GetRelayStatus_SelfHostWithARelayAndACredential_IsReady()
  {
    var result = await Controller(Edition.SelfHost).GetRelayStatus(Relay(true, true).Object, CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(new NotificationsController.RelayStatus(true, true));
  }

  [Fact]
  public async Task GetRelayStatus_SelfHostWithARelayButNoCredential_IsConfiguredButNotReady()
  {
    var result = await Controller(Edition.SelfHost).GetRelayStatus(Relay(true, false).Object, CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(new NotificationsController.RelayStatus(true, false));
  }

  [Fact]
  public async Task GetRelayStatus_Hosted_IsNeitherAndAsksNothing()
  {
    var relay = Relay(true, true);

    var result = await Controller(Edition.Hosted).GetRelayStatus(relay.Object, CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(new NotificationsController.RelayStatus(false, false));
    relay.Verify(r => r.IsReadyAsync(It.IsAny<CancellationToken>()), Times.Never);
  }
}
