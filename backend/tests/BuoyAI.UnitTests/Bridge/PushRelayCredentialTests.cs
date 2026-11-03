using System.Net;
using System.Security.Cryptography;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Services.Bridge;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuoyAI.UnitTests.Bridge;

/// <summary>The relay wants the per-server credential from the Cloud pairing the Cloud PushRelay service.</summary>
public class PushRelayCredentialTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }

    private static string PhoneKey()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return CloudPairingService.ExportPoint(key);
    }

    private static PushRelaySender Sender(Capture capture, Dictionary<string, string?> settings, IServiceScopeFactory? scopes = null)
    {
        settings["PushRelay:Url"] = settings.GetValueOrDefault("PushRelay:Url") ?? "https://relay.test";
        return new PushRelaySender(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new FakeFactory(capture),
            NullLogger<PushRelaySender>.Instance, scopes);
    }

    private static readonly IReadOnlyDictionary<string, string> Data = new Dictionary<string, string> { ["type"] = "x" };

    [Fact]
    public async Task WithoutAnyCredential_RequestsGoOutAsBefore()
    {
        var capture = new Capture();
        await Sender(capture, new()).SendAsync(new[] { new RelayDevice("p1", PhoneKey()) }, "t", "b", Data);
        capture.Authorizations.Should().Equal(new string?[] { null });
    }

    [Fact]
    public async Task AConfiguredServerKey_IsSentAsBearer()
    {
        var capture = new Capture();
        await Sender(capture, new() { ["PushRelay:ServerKey"] = "bmk_abc_def" }).SendAsync(new[] { new RelayDevice("p1", PhoneKey()) }, "t", "b", Data);
        capture.Authorizations.Should().Equal("Bearer bmk_abc_def");
    }

    [Fact]
    public async Task TheStoredCloudPairing_IsUsedWhenThereIsNoSetting()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var provider = new ServiceCollection().AddSingleton(h.Pairing).BuildServiceProvider();
        var capture = new Capture();

        await Sender(capture, new() { ["PushRelay:Url"] = "https://cloud.test" }, provider.GetRequiredService<IServiceScopeFactory>())
            .SendAsync(new[] { new RelayDevice("p1", PhoneKey()) }, "t", "b", Data);

        capture.Authorizations.Should().Equal("Bearer " + FakeCloud.Credential);
    }
    // ---- ready to send (the owner's setup checklist)

    [Fact]
    public async Task IsReady_WithoutARelayAddress_IsFalse()
    {
        var sender = new PushRelaySender(new ConfigurationBuilder().Build(), new FakeFactory(new Capture()),
            NullLogger<PushRelaySender>.Instance);

        (await sender.IsReadyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsReady_WithARelayButNoCredential_IsFalse()
    {
        (await Sender(new Capture(), new()).IsReadyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsReady_WithAServerKey_IsTrue()
    {
        (await Sender(new Capture(), new() { ["PushRelay:ServerKey"] = "bmk_abc_def" }).IsReadyAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task IsReady_WithTheCloudLinkAndTheCloudAsRelay_IsTrue()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var provider = new ServiceCollection().AddSingleton(h.Pairing).BuildServiceProvider();

        var sender = Sender(new Capture(), new() { ["PushRelay:Url"] = "https://cloud.test" }, provider.GetRequiredService<IServiceScopeFactory>());

        (await sender.IsReadyAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task IsReady_WithTheCloudLinkButAnotherRelayHost_IsFalse()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var provider = new ServiceCollection().AddSingleton(h.Pairing).BuildServiceProvider();

        var sender = Sender(new Capture(), new() { ["PushRelay:Url"] = "https://relay.elsewhere.test" }, provider.GetRequiredService<IServiceScopeFactory>());

        (await sender.IsReadyAsync()).Should().BeFalse();
    }
}
