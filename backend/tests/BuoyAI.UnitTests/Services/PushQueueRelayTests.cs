using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>The quiet-hours queue drain must run on a self-hosted server that has only the push relay.</summary>
public class PushQueueRelayTests
{
    private sealed class MeterFactory : System.Diagnostics.Metrics.IMeterFactory
    {
        public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options) => new(options);
        public void Dispose() { }
    }

    private static async Task<int> RunAsync(bool fcmEnabled, bool relayEnabled)
    {
        var push = new Mock<IPushNotificationService>();
        push.Setup(p => p.DeliverDueQueuedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        var services = new ServiceCollection().AddSingleton(push.Object).BuildServiceProvider();
        var fcm = new Mock<IFcmSender>();
        fcm.Setup(f => f.IsEnabled).Returns(fcmEnabled);
        var relay = new Mock<IPushRelaySender>();
        relay.Setup(r => r.IsEnabled).Returns(relayEnabled);

        var service = new PushQueueDeliveryService(
            services.GetRequiredService<IServiceScopeFactory>(), fcm.Object,
            NullLogger<PushQueueDeliveryService>.Instance, new BuoyAiMetrics(new MeterFactory()),
            relay.Object, TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(400);
        await service.StopAsync(CancellationToken.None);
        return push.Invocations.Count(i => i.Method.Name == nameof(IPushNotificationService.DeliverDueQueuedAsync));
    }

    [Fact]
    public async Task RelayOnly_StillDrainsTheQueue() => (await RunAsync(fcmEnabled: false, relayEnabled: true)).Should().BeGreaterThan(0);

    [Fact]
    public async Task NeitherSender_Idles() => (await RunAsync(fcmEnabled: false, relayEnabled: false)).Should().Be(0);
}
