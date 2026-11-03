using System.Net;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services.Ai;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

internal sealed class CloudStub : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Seen { get; } = new();
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json("{}");

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Seen.Add((request, request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
        return Respond(request);
    }
}

public class CloudAiProviderTests
{
    private const string Url = "https://cloud.test";

    private static AiProviderOptions Options(string url = Url, string credential = "pair-secret-123") =>
        AiProviderOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "buoy-cloud", ["Ai:CloudUrl"] = url, ["Ai:CloudCredential"] = credential,
        }).Build(), new EditionInfo(Edition.SelfHost));

    private static (CloudAiProvider Provider, CloudAiGatewayClient Client, CloudStub Stub) Make(AiProviderOptions? options = null)
    {
        var stub = new CloudStub();
        var opts = options ?? Options();
        var client = new CloudAiGatewayClient(new HttpClient(stub), opts);
        return (new CloudAiProvider(opts, client), client, stub);
    }

    private const string Reply = """
        {"id":"buoy-1","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"21:0.9"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":812,"completion_tokens":9,"total_tokens":821},
         "buoy":{"usedMicros":5000,"allowanceMicros":1000000,"percent":0,"state":"ok","callCostMicros":347}}
        """;

    [Fact]
    public async Task ACall_GoesToTheGatewayWithTheCredential_TheFeature_AndARequestId()
    {
        var (provider, _, stub) = Make();
        stub.Respond = _ => CloudStub.Json(Reply);

        var result = await provider.GenerateTextAsync(new AiTextRequest("categorize this", "ignored", "categorization"));

        var (request, body) = stub.Seen.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://cloud.test/v1/chat/completions");
        request.Headers.Authorization!.ToString().Should().Be("Bearer pair-secret-123");
        request.Headers.GetValues("X-Buoy-Feature").Single().Should().Be("categorization");
        request.Headers.GetValues("X-Request-Id").Single().Should().NotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("categorize this");
        doc.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(1024, "a short feature must not ask the gateway to hold a long answer's cost");
        doc.RootElement.TryGetProperty("model", out _).Should().BeFalse("the gateway picks the model");

        result.Text.Should().Be("21:0.9");
        result.InputTokens.Should().Be(812);
        result.OutputTokens.Should().Be(9);
        result.CostUsd.Should().Be(0.000347m, "the gateway's micro-dollars, exactly");
    }

    [Fact]
    public async Task EachCall_GetsItsOwnRequestId()
    {
        var (provider, _, stub) = Make();
        stub.Respond = _ => CloudStub.Json(Reply);

        await provider.GenerateTextAsync(new AiTextRequest("a", "m", "insights"));
        await provider.GenerateTextAsync(new AiTextRequest("b", "m", "insights"));

        stub.Seen.Select(s => s.Request.Headers.GetValues("X-Request-Id").Single()).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task AFeatureWithoutAShortAnswerCeiling_SendsNoMaxTokens_SoTheGatewayCeilingApplies()
    {
        var (provider, _, stub) = Make();
        stub.Respond = _ => CloudStub.Json(Reply);

        await provider.GenerateTextAsync(new AiTextRequest("a", "m", "insights"));

        using var doc = JsonDocument.Parse(stub.Seen[0].Body);
        doc.RootElement.GetProperty("max_tokens").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AllowanceUsedUp_IsATypedAnswer_WithTheGatewaysMessage_AndTheOfferToTurnOnPayg()
    {
        var (provider, _, stub) = Make();
        stub.Respond = _ => CloudStub.Json(
            """{"error":{"code":"allowance_exhausted","message":"You've used this period's AI allowance.","details":{"canEnablePayg":true}}}""",
            HttpStatusCode.PaymentRequired);

        var act = () => provider.GenerateTextAsync(new AiTextRequest("a", "m", "insights"));

        var ex = (await act.Should().ThrowAsync<AiAllowanceException>()).Which;
        ex.Code.Should().Be("allowance_exhausted");
        ex.CanEnablePayg.Should().BeTrue();
        ex.Message.Should().Be("You've used this period's AI allowance.");
        ex.Should().BeAssignableTo<AiUnavailableException>("callers that already degrade on AI being unavailable keep working");
    }

    [Fact]
    public async Task PaygCapReached_DoesNotOfferToTurnItOn()
    {
        var (provider, _, stub) = Make();
        stub.Respond = _ => CloudStub.Json(
            """{"error":{"code":"payg_cap_reached","message":"limit","details":{"canEnablePayg":false}}}""", HttpStatusCode.PaymentRequired);

        var ex = (await FluentActions.Awaiting(() => provider.GenerateTextAsync(new AiTextRequest("a", "m", "insights")))
            .Should().ThrowAsync<AiAllowanceException>()).Which;

        ex.Code.Should().Be("payg_cap_reached");
        ex.CanEnablePayg.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"code":"invalid_credential","message":"Pair the server again."}}""", "Pair the server again.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"metering_unavailable","message":"AI is paused."}}""", "AI is paused.")]
    [InlineData(HttpStatusCode.BadGateway, "<html>proxy error</html>", "BuoyAI Cloud returned an error (502).")]
    public async Task OtherFailures_AreAiUnavailable_WithAPlainMessage(HttpStatusCode status, string body, string message)
    {
        var (provider, _, stub) = Make();
        stub.Respond = _ => CloudStub.Json(body, status);

        var ex = (await FluentActions.Awaiting(() => provider.GenerateTextAsync(new AiTextRequest("a", "m", "insights")))
            .Should().ThrowAsync<AiUnavailableException>()).Which;

        ex.Should().NotBeOfType<AiAllowanceException>();
        ex.Message.Should().Be(message);
    }

    [Fact]
    public async Task AnUnreachableGateway_IsAiUnavailable_NotACrash()
    {
        var (provider, _, stub) = Make();
        stub.Respond = _ => throw new HttpRequestException("connection refused");

        var act = () => provider.GenerateTextAsync(new AiTextRequest("a", "m", "insights"));

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Message.Should().Contain("could not be reached");
    }

    [Fact]
    public async Task Attachments_AreRefusedBeforeAnythingIsSent()
    {
        var (provider, _, stub) = Make();

        var act = () => provider.GenerateTextAsync(new AiTextRequest("a", "m", "receipts", new[] { ("image/png", new byte[] { 1 }) }));

        await act.Should().ThrowAsync<AiUnavailableException>();
        stub.Seen.Should().BeEmpty();
    }

    [Fact]
    public void Setup_NeedsAnAddressAndACredential()
    {
        Make(Options(url: "")).Provider.IsConfigured.Should().BeFalse();
        Make(Options(url: "ftp://x")).Provider.IsConfigured.Should().BeFalse();
        Make(Options(credential: "")).Provider.IsConfigured.Should().BeFalse();
        Make(Options()).Provider.IsConfigured.Should().BeTrue();
        Make(Options(url: "http://cloud.example.com")).Provider.IsConfigured.Should().BeFalse(); // credential must not travel in cleartext
        Make(Options(url: "http://localhost:8080")).Provider.IsConfigured.Should().BeTrue();
        Make(Options(url: "http://host.docker.internal:8080")).Provider.IsConfigured.Should().BeTrue();
        Options(url: "").SetupProblem.Should().Contain("Ai:CloudUrl");
        Options(credential: "").SetupProblem.Should().Contain("Ai:CloudCredential");
    }

    [Fact]
    public async Task NotSetUp_ThrowsAndNeverCallsOut()
    {
        var (provider, _, stub) = Make(Options(credential: ""));

        var act = () => provider.GenerateTextAsync(new AiTextRequest("a", "m", "insights"));

        await act.Should().ThrowAsync<AiUnavailableException>();
        stub.Seen.Should().BeEmpty();
    }

    [Fact]
    public void ItDoesTextOnly_SoChatAgentsAndAttachmentsStayHidden()
    {
        Make().Provider.Capabilities.Should().Be(new AiCapabilities(true, false, false, false));
    }

    [Fact]
    public async Task TheUsageSummary_IsReadFromTheGateway_AndMarkedAsCloud()
    {
        var (_, client, stub) = Make();
        stub.Respond = _ => CloudStub.Json("""
            {"periodStart":"2026-10-01T00:00:00Z","periodEnd":"2026-11-01T00:00:00Z","allowanceMicros":1500000,"usedMicros":1200000,"percent":80,
             "state":"warning","blocked":false,"projectedMicros":2000000,
             "payg":{"available":true,"enabled":false,"capMicros":0,"usedMicros":0,"maxCapMicros":20000000},
             "byFeature":[{"feature":"categorization","calls":40,"costMicros":900000},{"feature":"insights","calls":3,"costMicros":300000}]}
            """);

        var summary = await client.GetUsageAsync(default);

        stub.Seen[0].Request.Method.Should().Be(HttpMethod.Get);
        stub.Seen[0].Request.RequestUri!.AbsolutePath.Should().Be("/v1/usage");
        summary.Source.Should().Be("cloud");
        summary.Unlimited.Should().BeFalse();
        summary.AllowanceMicros.Should().Be(1_500_000);
        summary.UsedMicros.Should().Be(1_200_000);
        summary.Percent.Should().Be(80);
        summary.State.Should().Be("warning");
        summary.Payg.Available.Should().BeTrue();
        summary.Payg.MaxCapMicros.Should().Be(20_000_000);
        summary.ByFeature.Should().BeEquivalentTo(new[]
        {
            new AiFeatureUsage("categorization", 40, 900_000), new AiFeatureUsage("insights", 3, 300_000),
        });
        summary.PeriodEnd.Should().Be(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task SettingPayg_PutsTheChoiceAndTheCapInMicros()
    {
        var (_, client, stub) = Make();
        stub.Respond = _ => CloudStub.Json("""{"periodStart":"2026-10-01T00:00:00Z","periodEnd":"2026-11-01T00:00:00Z","allowanceMicros":1,"usedMicros":0,"percent":0,"state":"ok","blocked":false,"projectedMicros":0,"payg":{"available":true,"enabled":true,"capMicros":5000000,"usedMicros":0,"maxCapMicros":20000000},"byFeature":[]}""");

        var summary = await client.SetPaygAsync(true, 5_000_000, default);

        stub.Seen[0].Request.Method.Should().Be(HttpMethod.Put);
        stub.Seen[0].Request.RequestUri!.AbsolutePath.Should().Be("/v1/usage/payg");
        using var doc = JsonDocument.Parse(stub.Seen[0].Body);
        doc.RootElement.GetProperty("enabled").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("capMicros").GetInt64().Should().Be(5_000_000);
        summary.Payg.Enabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("bad_cap")]
    [InlineData("payg_unavailable")]
    public async Task AGatewayRefusalOfTheCap_IsAValidationErrorTheScreenCanShow(string code)
    {
        var (_, client, stub) = Make();
        stub.Respond = _ => CloudStub.Json("""{"error":{"code":"CODE","message":"Not allowed."}}""".Replace("CODE", code), HttpStatusCode.BadRequest);

        var act = () => client.SetPaygAsync(true, 1, default);

        (await act.Should().ThrowAsync<DomainValidationException>()).Which.Message.Should().Be("Not allowed.");
    }

    [Fact]
    public void TheCredential_NeverAppearsInAnyOptionsDump()
    {
        var text = Options().ToString() + System.Text.Json.JsonSerializer.Serialize(new { Options().Provider, Options().CloudUrl });
        text.Should().NotContain("pair-secret-123");
    }
}

public class CloudAiContainerTests
{
    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            settings.ToDictionary(s => s.Key, s => s.Value)).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddInfrastructure(config, skipDbContext: true);
        services.AddDbContext<BuoyAIDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddDbContextFactory<BuoyAIDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()), ServiceLifetime.Scoped);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void BuoyCloud_IsWiredAsTheProvider_WithEmbeddingsOff_AndTheGatewayClientRegistered()
    {
        using var sp = Build(("BuoyMoney:Edition", "selfhost"), ("Ai:Provider", "buoy-cloud"),
            ("Ai:CloudUrl", "https://cloud.test"), ("Ai:CloudCredential", "secret"));

        var ai = sp.GetRequiredService<IAiProvider>();
        ai.Should().BeOfType<MeteredAiProvider>().Which.Inner.Should().BeOfType<CloudAiProvider>();
        ai.IsConfigured.Should().BeTrue();
        ai.Id.Should().Be("buoy-cloud");
        sp.GetRequiredService<IEmbeddingClient>().IsEnabled.Should().BeFalse();
        sp.GetService<CloudAiGatewayClient>().Should().NotBeNull();
    }

    [Fact]
    public void WithoutTheCloudProvider_ThereIsNoGatewayClient()
    {
        using var sp = Build(("BuoyMoney:Edition", "selfhost"));
        sp.GetService<CloudAiGatewayClient>().Should().BeNull();
    }

    [Fact]
    public void BuoyCloudWithNoCredential_StartsButAIIsNotConfigured()
    {
        using var sp = Build(("BuoyMoney:Edition", "selfhost"), ("Ai:Provider", "buoy-cloud"), ("Ai:CloudUrl", "https://cloud.test"));
        sp.GetRequiredService<IAiProvider>().IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void AMisspelledProvider_StillStopsStartup_AndTheMessageListsBuoyCloud()
    {
        var act = () => Build(("Ai:Provider", "buoy-cloudd"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*buoy-cloud*");
    }
}

public class AiUsageSummaryServiceTests
{
    private static BuoyAIDbContext Db() =>
        new(new DbContextOptionsBuilder<BuoyAIDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Mock<IEntitlementService> Entitlements(SubscriptionTier tier)
    {
        var e = new Mock<IEntitlementService>();
        e.Setup(x => x.GetTierOrStandardAsync(It.IsAny<int>())).ReturnsAsync(tier);
        e.Setup(x => x.GetAiLimits(It.IsAny<SubscriptionTier>())).Returns(BuoyAI.Core.Entitlements.AiLimits.Default.For(tier));
        return e;
    }

    private static async Task Spend(BuoyAIDbContext db, int userId, string feature, decimal cost, DateTime? at = null)
    {
        db.AIUsageRecords.Add(new AIUsageRecord { UserId = userId, Model = "m", RequestContext = feature, EstimatedCost = cost, Timestamp = at ?? DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Hosted_ShowsTheTiersMonthlyFigure_ThisPeriodsSpend_AndABreakdown()
    {
        using var db = Db();
        await Spend(db, 1, "chat", 3.00m);
        await Spend(db, 1, "categorization", 0.50m);
        await Spend(db, 1, "chat", 0.25m);
        await Spend(db, 2, "chat", 99m); // someone else
        var service = new AiUsageSummaryService(db, Entitlements(SubscriptionTier.Standard).Object, new EditionInfo(Edition.Hosted));

        var summary = await service.GetAsync(1);

        summary.Source.Should().Be("local");
        summary.Unlimited.Should().BeFalse();
        summary.AllowanceMicros.Should().Be(5_000_000, "Standard's monthly figure is $5");
        summary.UsedMicros.Should().Be(3_750_000);
        summary.Percent.Should().Be(75);
        summary.State.Should().Be("ok");
        summary.Blocked.Should().BeFalse();
        summary.Payg.Available.Should().BeFalse();
        summary.ByFeature.Should().BeEquivalentTo(new[] { new AiFeatureUsage("chat", 2, 3_250_000), new AiFeatureUsage("categorization", 1, 500_000) },
            o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData(4.00, 80, "warning")]
    [InlineData(5.00, 100, "over")]
    public async Task Hosted_StateFollowsThePercent(double spend, int percent, string state)
    {
        using var db = Db();
        await Spend(db, 1, "chat", (decimal)spend);
        var service = new AiUsageSummaryService(db, Entitlements(SubscriptionTier.Standard).Object, new EditionInfo(Edition.Hosted));

        var summary = await service.GetAsync(1);

        summary.Percent.Should().Be(percent);
        summary.State.Should().Be(state);
        summary.Blocked.Should().BeFalse("the hosted monthly figure is shown and warned on, not enforced");
    }

    [Fact]
    public async Task SelfHost_WithoutCloud_IsUnlimited_StatisticsOnly()
    {
        using var db = Db();
        await Spend(db, 1, "insights", 12m);
        var service = new AiUsageSummaryService(db, Entitlements(SubscriptionTier.Premium).Object, new EditionInfo(Edition.SelfHost));

        var summary = await service.GetAsync(1);

        summary.Unlimited.Should().BeTrue();
        summary.AllowanceMicros.Should().Be(0);
        summary.UsedMicros.Should().Be(12_000_000);
        summary.State.Should().Be("ok");
        summary.Blocked.Should().BeFalse();
    }

    [Fact]
    public async Task SpendFromBeforeThePeriod_IsLeftOut()
    {
        using var db = Db();
        await Spend(db, 1, "chat", 4m, DateTime.UtcNow.AddMonths(-2));
        var service = new AiUsageSummaryService(db, Entitlements(SubscriptionTier.Standard).Object, new EditionInfo(Edition.Hosted));

        (await service.GetAsync(1)).UsedMicros.Should().Be(0);
    }

    [Fact]
    public async Task WithCloud_TheGatewayIsAskedAndLocalRowsAreIgnored()
    {
        using var db = Db();
        await Spend(db, 1, "chat", 4m);
        var stub = new CloudStub { Respond = _ => CloudStub.Json("""{"periodStart":"2026-10-01T00:00:00Z","periodEnd":"2026-11-01T00:00:00Z","allowanceMicros":1500000,"usedMicros":7,"percent":0,"state":"ok","blocked":false,"projectedMicros":7,"payg":{"available":false,"enabled":false,"capMicros":0,"usedMicros":0,"maxCapMicros":0},"byFeature":[]}""") };
        var options = AiProviderOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Ai:Provider"] = "buoy-cloud", ["Ai:CloudUrl"] = "https://cloud.test", ["Ai:CloudCredential"] = "s" }).Build(), new EditionInfo(Edition.SelfHost));
        var service = new AiUsageSummaryService(db, Entitlements(SubscriptionTier.Premium).Object, new EditionInfo(Edition.SelfHost),
            new CloudAiGatewayClient(new HttpClient(stub), options));

        var summary = await service.GetAsync(1);

        summary.Source.Should().Be("cloud");
        summary.UsedMicros.Should().Be(7);
    }

    [Fact]
    public async Task PaygWithoutCloud_IsRefusedPlainly()
    {
        using var db = Db();
        var service = new AiUsageSummaryService(db, Entitlements(SubscriptionTier.Premium).Object, new EditionInfo(Edition.Hosted));

        var act = () => service.SetPaygAsync(1, true, 1_000_000);

        (await act.Should().ThrowAsync<DomainValidationException>()).Which.Message.Should().Contain("BuoyAI Cloud");
    }
}
