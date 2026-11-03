using System.Reflection;
using System.Security.Cryptography;
using BuoyAI.API.Controllers;
using BuoyAI.API.Filters;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Services.Bridge;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Bridge;

/// <summary>The Cloud keys banks by server, so this server must tie each bank to the user whose page made it.</summary>
public class BridgeOwnershipTests
{
    private const int OtherUserId = 2;

    private static BridgeHarness TwoUsers()
    {
        var h = new BridgeHarness();
        h.Db.Users.Add(new User { Id = OtherUserId, Username = "sam", Email = "sam@home.test", FirstName = "Sam", PasswordHash = "x" });
        h.Db.SaveChanges();
        return h;
    }

    [Fact]
    public async Task EachUserAdoptsOnlyTheBankTheirOwnPageMade()
    {
        using var h = TwoUsers();
        await h.PairAsync();
        var kats = h.AddItem(i => i.Name = "Kat Bank");
        var sams = h.AddItem(i => i.Name = "Sam Bank");
        kats.Pages.Enqueue(BridgeFixtures.OnePage());

        // Both banks exist at the Cloud before either person comes back.
        h.Cloud.NextLinkItem = kats;
        var katStart = await h.Aggregator.StartConnectionAsync(BridgeHarness.UserId, "web");
        h.Cloud.NextLinkItem = sams;
        var samStart = await h.Aggregator.StartConnectionAsync(OtherUserId, "web");

        var samResult = await h.Aggregator.CompleteConnectionAsync(OtherUserId, samStart.Value);
        var katResult = await h.Aggregator.CompleteConnectionAsync(BridgeHarness.UserId, katStart.Value);

        samResult.Success.Should().BeTrue();
        katResult.Success.Should().BeTrue();
        var rows = await h.Db.PlaidConnections.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(c => c.InstitutionName == "Kat Bank").UserId.Should().Be(BridgeHarness.UserId);
        rows.Single(c => c.InstitutionName == "Sam Bank").UserId.Should().Be(OtherUserId);
    }

    [Fact]
    public async Task AUserWhoStartedNothing_AdoptsNothing_EvenWithSomeoneElsesPageAddress()
    {
        using var h = TwoUsers();
        await h.PairAsync();
        var kats = h.AddItem();
        kats.Pages.Enqueue(BridgeFixtures.OnePage());
        h.Cloud.NextLinkItem = kats;
        var katStart = await h.Aggregator.StartConnectionAsync(BridgeHarness.UserId, "web");

        var stolen = await h.Aggregator.CompleteConnectionAsync(OtherUserId, katStart.Value);
        var guess = await h.Aggregator.CompleteConnectionAsync(OtherUserId, "s1");

        stolen.Success.Should().BeFalse();
        guess.Success.Should().BeFalse();
        (await h.Db.PlaidConnections.CountAsync()).Should().Be(0);
        (await h.Db.Accounts.CountAsync()).Should().Be(0);

        var katResult = await h.Aggregator.CompleteConnectionAsync(BridgeHarness.UserId, katStart.Value);
        katResult.Success.Should().BeTrue();
        (await h.Db.PlaidConnections.AsNoTracking().SingleAsync()).UserId.Should().Be(BridgeHarness.UserId);
    }

    [Fact]
    public async Task APageThatHasNotFinished_AdoptsNothingYet_AndCanBeCompletedLater()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        var start = await h.Aggregator.StartConnectionAsync(BridgeHarness.UserId, "web");

        var early = await h.Aggregator.CompleteConnectionAsync(BridgeHarness.UserId, start.Value);
        early.Success.Should().BeFalse();
        early.Message.Should().Contain("No new bank connection");
        (await h.Db.PlaidConnections.CountAsync()).Should().Be(0);

        // The person finishes at the bank; the fake now maps the same page to the bank.
        h.Cloud.NextLinkItem = item;
        var again = await h.Aggregator.StartConnectionAsync(BridgeHarness.UserId, "web");
        (await h.Aggregator.CompleteConnectionAsync(BridgeHarness.UserId, again.Value)).Success.Should().BeTrue();
    }

    [Fact]
    public async Task TheArtifactMayBeThePageAddressTheSessionIdOrAnythingElse()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        h.Cloud.NextLinkItem = item;
        await h.Aggregator.StartConnectionAsync(BridgeHarness.UserId, "web");

        (await h.Aggregator.CompleteConnectionAsync(BridgeHarness.UserId, "done")).Success.Should().BeTrue();
    }
}

public class BridgeReconnectTests
{
    [Fact]
    public async Task Reconnect_SignsInAgainForTheSameBank_AndTheConnectionWorksAgain()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem(i => i.Status = "login_required");
        var connection = await h.SeedConnectionAsync(item, c =>
        {
            c.Status = PlaidItemStatus.LoginRequired;
            c.RequiresUserAction = true;
            c.ConsecutiveFailures = 2;
            c.LastTransactionSyncAt = null;
        });
        item.Pages.Enqueue(BridgeFixtures.OnePage());

        var start = await h.Aggregator.StartReconnectAsync(BridgeHarness.UserId, connection.Id, "web");
        var result = await h.Aggregator.CompleteConnectionAsync(BridgeHarness.UserId, start.Value);

        result.Success.Should().BeTrue(result.Message);
        var saved = await h.ReloadAsync(connection.Id);
        saved.Status.Should().Be(PlaidItemStatus.Active);
        saved.RequiresUserAction.Should().BeFalse();
        saved.ConsecutiveFailures.Should().Be(0);
        (await h.Db.PlaidConnections.CountAsync()).Should().Be(1, "a reconnect keeps the same connection and makes no second login");
        (await h.Db.Transactions.CountAsync()).Should().Be(3);
        h.Cloud.Calls.Should().Contain("POST /bridge/v1/link/start");
    }

    [Fact]
    public async Task Reconnect_IsRefusedForSomeoneElsesConnection()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var connection = await h.SeedConnectionAsync(h.AddItem());

        var act = () => h.Aggregator.StartReconnectAsync(99, connection.Id, "web");

        await act.Should().ThrowAsync<BuoyAI.Core.Exceptions.DomainValidationException>();
    }
}

public class BridgeRetryLaterTests
{
    private static Task<int> SyncAsync(BridgeHarness h, PlaidConnection c) =>
        h.Aggregator.SyncConnectionTransactionsAsync(c, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow);

    [Fact]
    public async Task ACloudThrottle_IsNeverCountedAsAFailure_SoTheConnectionStaysActive()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        h.Cloud.ThrottleAfterPulls = 0;
        var connection = await h.SeedConnectionAsync(item);

        for (var i = 0; i < 6; i++)
        {
            var act = () => SyncAsync(h, connection);
            var ex = (await act.Should().ThrowAsync<BridgeRetryLaterException>()).Which;
            await h.Aggregator.HandleSyncFailureAsync(connection.Id, ex.Message);
        }

        var saved = await h.ReloadAsync(connection.Id);
        saved.Status.Should().Be(PlaidItemStatus.Active);
        saved.ConsecutiveFailures.Should().Be(0);
        saved.RequiresUserAction.Should().BeFalse();
        saved.LastSyncError.Should().Contain("Too many pulls").And.NotContain("BRIDGE_RETRY");
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(It.IsAny<PlaidConnection>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ARetryAfterLongerThanTheMinimumInterval_PushesTheNextAttemptOut()
    {
        using var h = new BridgeHarness(o => o.MinSyncIntervalMinutes = 1);
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        h.Cloud.ThrottleAfterPulls = 0;
        var connection = await h.SeedConnectionAsync(item);

        var act = () => SyncAsync(h, connection);
        var ex = (await act.Should().ThrowAsync<BridgeRetryLaterException>()).Which;
        await h.Aggregator.HandleSyncFailureAsync(connection.Id, ex.Message);

        var saved = await h.ReloadAsync(connection.Id);
        // The Cloud said 120 seconds and the interval is 60: the attempt is stamped a minute ahead so the connection is due in 120.
        saved.LastSyncAttemptAt.Should().BeAfter(DateTime.UtcNow.AddSeconds(30));
    }

    [Fact]
    public async Task AThrottleInTheMiddleOfACatchUp_KeepsWhatWasApplied_AndStopsCleanly()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        for (var i = 0; i < 3; i++)
            item.Pages.Enqueue(new FakePage { Accounts = new JsonArrayHolder().Accounts, Added = new JsonArrayHolder().Tx("t" + i), HasMore = true });
        h.Cloud.ThrottleAfterPulls = 1;
        var connection = await h.SeedConnectionAsync(item, c => c.LastTransactionSyncAt = null);

        var count = await SyncAsync(h, connection);

        count.Should().Be(1);
        item.Pages.Should().HaveCount(2, "the first page was acknowledged, the rest wait for the next sync");
        h.Cloud.Calls.Count(c => c.EndsWith("/ack")).Should().Be(1);
    }

    private sealed class JsonArrayHolder
    {
        public System.Text.Json.Nodes.JsonArray Accounts => new(BridgeFixtures.Account("acc-1", "A"));
        public System.Text.Json.Nodes.JsonArray Tx(string id) => new(BridgeFixtures.Tx(id, "acc-1", 1m, "x"));
    }
}

public class PushRelayCredentialHostTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Accepted));
        }
    }

    private static async Task<List<string?>> SendAsync(string relayUrl, BridgeHarness h)
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var provider = new ServiceCollection().AddSingleton(h.Pairing).BuildServiceProvider();
        var capture = new Capture();
        var sender = new PushRelaySender(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PushRelay:Url"] = relayUrl }).Build(),
            new FakeFactory(capture), NullLogger<PushRelaySender>.Instance, provider.GetRequiredService<IServiceScopeFactory>());
        await sender.SendAsync(new[] { new RelayDevice("p1", CloudPairingService.ExportPoint(key)) }, "t", "b",
            new Dictionary<string, string> { ["type"] = "x" });
        return capture.Authorizations;
    }

    [Fact]
    public async Task TheCloudCredential_GoesToTheCloudHost()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();

        (await SendAsync("https://cloud.test/relay", h)).Should().Equal("Bearer " + FakeCloud.Credential);
    }

    [Theory]
    [InlineData("https://relay.elsewhere.test")]
    [InlineData("http://cloud.test")]
    public async Task TheCloudCredential_IsNeverSentToAnotherHost_OrOverPlainHttp(string relay)
    {
        using var h = new BridgeHarness();
        await h.PairAsync();

        (await SendAsync(relay, h)).Should().Equal(new string?[] { null });
    }
}

public class SelfHostOnlyTests
{
    private static async Task<(int Status, bool ReachedNext)> RunGateAsync(Edition edition)
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new SelfHostOnlyAttribute()), "test"));
        var reachedNext = false;
        var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        builder.UseSelfHostOnlyGate(new EditionInfo(edition));
        builder.Run(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        });
        await builder.Build()(http);
        return (http.Response.StatusCode, reachedNext);
    }

    [Fact]
    public async Task OnTheHostedService_TheCloudPairingRoutesAnswerNotFound()
    {
        var (status, reachedNext) = await RunGateAsync(Edition.Hosted);

        status.Should().Be(404);
        reachedNext.Should().BeFalse();
    }

    [Fact]
    public async Task OnSelfHost_TheyAreReachable()
    {
        var (_, reachedNext) = await RunGateAsync(Edition.SelfHost);

        reachedNext.Should().BeTrue();
    }

    [Fact]
    public void TheCloudPairingController_IsMarkedSelfHostOnly() =>
        typeof(CloudBridgeController).GetCustomAttribute<SelfHostOnlyAttribute>().Should().NotBeNull();
}
