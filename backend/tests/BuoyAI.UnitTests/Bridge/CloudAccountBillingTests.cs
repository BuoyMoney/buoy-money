using System.Reflection;
using System.Text.Json.Nodes;
using BuoyAI.API.Controllers;
using BuoyAI.API.Filters;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Infrastructure.Services.Bridge;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BuoyAI.UnitTests.Bridge;

/// <summary>
/// A self-hosted server's billing screen: it has no Stripe customer, so it asks the Cloud bridge, as the paired server,
/// for the account it is paired to. The server never sends a customer id.
/// </summary>
public class CloudAccountBillingTests
{
    private static JsonObject Summary() => new()
    {
        ["subscription"] = new JsonObject
        {
            ["subscriptionId"] = "sub_1", ["status"] = "active", ["interval"] = "yearly", ["founding"] = true,
            ["cancelAtPeriodEnd"] = false, ["products"] = new JsonArray(), ["items"] = new JsonArray(), ["pending"] = new JsonArray(),
        },
        ["upcoming"] = new JsonObject
        {
            ["subscriptionId"] = "sub_1", ["status"] = "active", ["interval"] = "yearly", ["founding"] = true,
            ["cancelAtPeriodEnd"] = false, ["currency"] = "usd", ["totalCents"] = 2399,
            ["lines"] = new JsonArray(new JsonObject
            {
                ["kind"] = "product", ["product"] = "bank_sync", ["description"] = "Bank sync", ["quantity"] = 1,
                ["unitAmountCents"] = 2399, ["amountCents"] = 2399, ["proration"] = false,
            }),
        },
        ["lookupFailed"] = false,
    };

    private static CloudAccountBillingController ControllerFor(BridgeClient? client, bool https = false)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = https ? "https" : "http";
        http.Request.Host = new HostString("money.example.com");
        return new CloudAccountBillingController(client) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    [Fact]
    public async Task TheClient_ReadsThePairedAccountsSummary_AsTheSharedShape()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        h.Cloud.BillingSummary = Summary();

        var summary = await h.Client.GetBillingAsync();

        summary!.Subscription!.Founding.Should().BeTrue();
        summary.Upcoming!.TotalCents.Should().Be(2399);
        summary.Upcoming.Lines.Single().Product.Should().Be("bank_sync");
        summary.LookupFailed.Should().BeFalse();
        h.Cloud.Calls.Should().Contain("GET /bridge/v1/billing");
    }

    [Fact]
    public async Task TheClient_NamesNoCustomer_InAnyBillingCall()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        h.Cloud.BillingSummary = Summary();

        await h.Client.GetBillingAsync();
        await h.Client.CreateBillingPortalAsync("https://money.example.com/");

        h.Cloud.PortalBodies.Single().ToLowerInvariant().Should().NotContain("customer").And.NotContain("owner");
    }

    [Fact]
    public async Task ThePortal_ReturnsTheCloudsAddress_AndPassesTheHttpsReturnAddress()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();

        var url = await h.Client.CreateBillingPortalAsync("https://money.example.com/");

        url.Should().Be("https://billing.stripe.test/p/abc");
        h.Cloud.PortalBodies.Single().Should().Contain("https://money.example.com/");
    }

    [Fact]
    public async Task TheController_ReturnsTheSummaryDirectly()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        h.Cloud.BillingSummary = Summary();

        var result = await ControllerFor(h.Client).GetSummary(default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<CloudBillingSummary>()
            .Which.Upcoming!.Lines.Should().HaveCount(1);
    }

    [Fact]
    public async Task TheController_WhenTheCloudHasNoBilling_PassesThe404On()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        h.Cloud.BillingSummary = null;

        var result = await ControllerFor(h.Client).GetSummary(default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task TheController_WhenNothingIsPaired_SaysSo_WithoutBeingA401()
    {
        using var h = new BridgeHarness();

        var result = await ControllerFor(h.Client).GetSummary(default);

        // A 401 would make the app sign the person out of their own server.
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().NotBe(401);
    }

    [Fact]
    public async Task TheController_WhenTheCredentialIsRejected_Is409()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        h.Cloud.RejectEverything = true;

        var result = await ControllerFor(h.Client).GetSummary(default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task ThePortalEndpoint_SendsAnHttpsOriginBack_ButNotAPlainHttpOne()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();

        await ControllerFor(h.Client, https: true).OpenPortal(default);
        await ControllerFor(h.Client, https: false).OpenPortal(default);

        h.Cloud.PortalBodies[0].Should().Contain("https://money.example.com/");
        h.Cloud.PortalBodies[1].Should().NotContain("money.example.com");
    }

    [Fact]
    public async Task ThePortalEndpoint_ReturnsTheAddress()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();

        var result = await ControllerFor(h.Client).OpenPortal(default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value!.GetType().GetProperty("url")!.GetValue(ok.Value).Should().Be("https://billing.stripe.test/p/abc");
    }

    [Fact]
    public async Task WhereTheCloudIsNotConfigured_EveryRouteIs404()
    {
        var controller = ControllerFor(null);

        (await controller.GetSummary(default)).Should().BeOfType<NotFoundObjectResult>();
        (await controller.OpenPortal(default)).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void TheController_IsSelfHostOnly_AdminOnly_AndInheritsTheBase()
    {
        typeof(CloudAccountBillingController).GetCustomAttribute<SelfHostOnlyAttribute>().Should().NotBeNull();
        typeof(CloudAccountBillingController).GetCustomAttribute<AuthorizeAttribute>()!.Roles.Should().Be("Admin");
        typeof(CloudAccountBillingController).BaseType.Should().Be(typeof(BaseController));
        typeof(CloudAccountBillingController).GetCustomAttributes<RouteAttribute>(inherit: false).Select(r => r.Template).Should().Contain("api/cloud/billing");
    }
}
