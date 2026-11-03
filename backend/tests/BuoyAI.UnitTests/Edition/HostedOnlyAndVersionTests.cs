using System.Reflection;
using BuoyAI.API.Controllers;
using BuoyAI.API.Filters;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.EditionSetting;

public class HostedOnlyAndVersionTests
{
    private static async Task<(int Status, bool ReachedNext)> RunGateAsync(Edition edition, bool hostedOnlyEndpoint)
    {
        var http = new DefaultHttpContext();
        var metadata = hostedOnlyEndpoint
            ? new EndpointMetadataCollection(new HostedOnlyAttribute())
            : new EndpointMetadataCollection();
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));
        var reachedNext = false;

        var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        builder.UseHostedOnlyGate(new EditionInfo(edition));
        builder.Run(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        });
        await builder.Build()(http);
        return (http.Response.StatusCode, reachedNext);
    }

    [Fact]
    public async Task Gate_LetsHostedRequestsThrough()
    {
        var (status, reachedNext) = await RunGateAsync(Edition.Hosted, hostedOnlyEndpoint: true);

        reachedNext.Should().BeTrue();
        status.Should().Be(200);
    }

    [Fact]
    public async Task Gate_AnswersNotFoundOnSelfHost_ForAHostedOnlyEndpoint_WithoutRunningAnythingAfterIt()
    {
        var (status, reachedNext) = await RunGateAsync(Edition.SelfHost, hostedOnlyEndpoint: true);

        status.Should().Be(404);
        reachedNext.Should().BeFalse("authentication and the controller must never see the request");
    }

    [Fact]
    public async Task Gate_LeavesOtherEndpointsAlone_OnSelfHost()
    {
        var (status, reachedNext) = await RunGateAsync(Edition.SelfHost, hostedOnlyEndpoint: false);

        reachedNext.Should().BeTrue();
        status.Should().Be(200);
    }

    [Theory]
    [InlineData(typeof(BillingController))]
    [InlineData(typeof(StripeWebhookController))]
    [InlineData(typeof(RevenueCatWebhookController))]
    [InlineData(typeof(ReferralController))]
    [InlineData(typeof(LeadsController))]
    [InlineData(typeof(ReservationsController))]
    [InlineData(typeof(PlaidController))]
    [InlineData(typeof(PlaidWebhookController))]
    [InlineData(typeof(PlaidUsageController))]
    [InlineData(typeof(BankRequestController))]
    [InlineData(typeof(BroadcastController))]
    [InlineData(typeof(BetaInvitesController))]
    [InlineData(typeof(CloudEventsController))]
    [InlineData(typeof(CloudServersController))]
    public void TheHostedControllers_AreMarkedHostedOnly(Type controller) =>
        controller.GetCustomAttribute<HostedOnlyAttribute>().Should().NotBeNull();

    [Theory]
    [InlineData(typeof(AuthController))]
    [InlineData(typeof(TransactionsController))]
    [InlineData(typeof(BudgetController))]
    [InlineData(typeof(ImportController))]
    [InlineData(typeof(ExportController))]
    [InlineData(typeof(SystemController))]
    public void TheHouseholdControllers_AreNotHostedOnly(Type controller) =>
        controller.GetCustomAttribute<HostedOnlyAttribute>().Should().BeNull();

    private static SystemController SystemFor(EditionInfo edition, Dictionary<string, string?>? values = null) =>
        new(Mock.Of<ICircuitBreakerService>(), NullLogger<SystemController>.Instance, edition,
            new ConfigurationBuilder().AddInMemoryCollection(values ?? new()).Build());

    [Fact]
    public void Version_ReportsTheBuildEditionAndAMinimumClient()
    {
        var result = SystemFor(new EditionInfo(Edition.SelfHost),
            new() { ["System:MinClientVersion"] = "1.6.0" }).GetVersion();

        var body = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        body.GetType().GetProperty("edition")!.GetValue(body).Should().Be("selfhost");
        body.GetType().GetProperty("minClientVersion")!.GetValue(body).Should().Be("1.6.0");
        body.GetType().GetProperty("version")!.GetValue(body).Should().BeOfType<string>()
            .Which.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }

    [Fact]
    public void Version_OnHostedWithNothingConfigured_AcceptsEveryClient()
    {
        var result = SystemFor(new EditionInfo(Edition.Hosted)).GetVersion();

        var body = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        body.GetType().GetProperty("edition")!.GetValue(body).Should().Be("hosted");
        body.GetType().GetProperty("minClientVersion")!.GetValue(body).Should().Be("1.0.0");
    }
}
