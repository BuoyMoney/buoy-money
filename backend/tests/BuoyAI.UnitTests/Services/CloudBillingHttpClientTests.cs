using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class CloudBillingHttpClientTests
{
  private sealed class StubHandler : HttpMessageHandler
  {
    public HttpRequestMessage? Request { get; private set; }
    public string Body { get; private set; } = "";
    public Func<HttpResponseMessage> Respond { get; set; } = () => new HttpResponseMessage(HttpStatusCode.OK);
    public Exception? Throw { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      Request = request;
      Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
      if (Throw != null) throw Throw;
      return Respond();
    }
  }

  private sealed class Factory : IHttpClientFactory
  {
    private readonly HttpMessageHandler _handler;
    public Factory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
  }

  private static (CloudBillingHttpClient Client, StubHandler Handler) Make(
    string baseUrl = "http://cloud-billing:8080", string key = "secret-key")
  {
    var handler = new StubHandler();
    var options = Options.Create(new CloudBillingOptions { Enabled = true, BaseUrl = baseUrl, ServiceKey = key });
    return (new CloudBillingHttpClient(new Factory(handler), options), handler);
  }

  private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
    new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  private const string StateJson =
    "{\"subscriptionId\":\"sub_1\",\"status\":\"active\",\"interval\":\"yearly\",\"founding\":true,\"currentPeriodEnd\":null," +
    "\"cancelAtPeriodEnd\":false,\"products\":[{\"product\":\"bank_sync\",\"quantity\":1,\"entitlements\":[\"bank_sync\"],\"endsAt\":null,\"quantityAfterPeriodEnd\":null}]," +
    "\"items\":[],\"pending\":[]}";

  [Fact]
  public async Task EveryCall_CarriesTheServiceKey()
  {
    var (client, handler) = Make();
    handler.Respond = () => Json(HttpStatusCode.OK, "{\"version\":\"v\",\"founding\":false,\"currency\":\"usd\",\"products\":[]}");

    await client.GetCatalogAsync(founding: true);

    handler.Request!.Headers.GetValues("X-Service-Key").Should().Equal("secret-key");
    handler.Request.RequestUri!.ToString().Should().Be("http://cloud-billing:8080/v1/catalog?founding=true");
  }

  [Fact]
  public async Task Checkout_PostsCamelCaseJson_AndTheIdempotencyKey()
  {
    var (client, handler) = Make();
    handler.Respond = () => Json(HttpStatusCode.OK, "{\"url\":\"https://checkout.stripe.com/x\",\"sessionId\":\"cs_1\"}");

    var response = await client.CheckoutAsync(new CloudCheckoutRequest(
      "cus_1", 7, "production", "yearly", true, [new CloudProductSelection("bank_sync")],
      "https://a/ok", "https://a/no", "idem-1"));

    response.Url.Should().Be("https://checkout.stripe.com/x");
    handler.Request!.Method.Should().Be(HttpMethod.Post);
    handler.Request.RequestUri!.AbsolutePath.Should().Be("/v1/checkout");
    handler.Request.Headers.GetValues("Idempotency-Key").Should().Equal("idem-1");
    using var doc = JsonDocument.Parse(handler.Body);
    doc.RootElement.GetProperty("customerId").GetString().Should().Be("cus_1");
    doc.RootElement.GetProperty("userId").GetInt32().Should().Be(7);
    doc.RootElement.GetProperty("products")[0].GetProperty("product").GetString().Should().Be("bank_sync");
  }

  [Fact]
  public async Task GetSubscription_EncodesTheQuery_AndReadsTheState()
  {
    var (client, handler) = Make();
    handler.Respond = () => Json(HttpStatusCode.OK, StateJson);

    var state = await client.GetSubscriptionAsync("cus a&b", "production");

    handler.Request!.RequestUri!.Query.Should().Be("?customerId=cus%20a%26b&environment=production");
    state!.Founding.Should().BeTrue();
    state.Products.Single().Product.Should().Be("bank_sync");
  }

  [Fact]
  public async Task NoSubscription_IsNull_NotAnError()
  {
    var (client, handler) = Make();
    handler.Respond = () => Json(HttpStatusCode.NotFound, "{\"code\":\"no_subscription\",\"message\":\"none\"}");
    (await client.GetSubscriptionAsync("cus_1", "production")).Should().BeNull();
  }

  [Fact]
  public async Task ARefusal_BecomesAnExceptionWithTheServicesCodeAndStatus()
  {
    var (client, handler) = Make();
    handler.Respond = () => Json(HttpStatusCode.Conflict, "{\"code\":\"already_owned\",\"message\":\"AI is already on this subscription.\"}");

    var act = () => client.ApplyAsync(new CloudChangeRequest("cus_1", "production", "add", "ai"));

    var ex = (await act.Should().ThrowAsync<CloudBillingException>()).Which;
    ex.Code.Should().Be("already_owned");
    ex.StatusCode.Should().Be(409);
    ex.Message.Should().Contain("already on");
  }

  [Fact]
  public async Task AWrongServiceKey_IsOurConfigurationProblem_NotTheCustomers()
  {
    var (client, handler) = Make();
    handler.Respond = () => Json(HttpStatusCode.Unauthorized, "{\"code\":\"unauthorized\",\"message\":\"Missing or wrong service key.\"}");

    var act = () => client.GetCatalogAsync(false);

    var ex = (await act.Should().ThrowAsync<CloudBillingException>()).Which;
    ex.Code.Should().Be("not_configured");
    ex.StatusCode.Should().Be(503);
  }

  [Fact]
  public async Task AProxyErrorPage_IsNeverEchoedToTheCustomer()
  {
    var (client, handler) = Make();
    handler.Respond = () => new HttpResponseMessage(HttpStatusCode.BadGateway)
    {
      Content = new StringContent("<html>upstream secret-internal-host:8080 refused</html>")
    };

    var act = () => client.GetCatalogAsync(false);

    var ex = (await act.Should().ThrowAsync<CloudBillingException>()).Which;
    ex.StatusCode.Should().Be(502);
    ex.Message.Should().NotContain("secret-internal-host");
  }

  [Fact]
  public async Task AnUnreachableService_Is503()
  {
    var (client, handler) = Make();
    handler.Throw = new HttpRequestException("connection refused");
    var act = () => client.GetCatalogAsync(false);
    var ex = (await act.Should().ThrowAsync<CloudBillingException>()).Which;
    ex.Code.Should().Be("unreachable");
    ex.StatusCode.Should().Be(503);
  }

  [Theory]
  [InlineData("", "key")]
  [InlineData("http://x", "")]
  [InlineData("  ", "  ")]
  public async Task WithoutAUrlAndKey_NothingIsSent(string url, string key)
  {
    var (client, handler) = Make(url, key);
    var act = () => client.GetCatalogAsync(false);
    (await act.Should().ThrowAsync<CloudBillingException>()).Which.Code.Should().Be("not_configured");
    handler.Request.Should().BeNull();
  }

  [Fact]
  public async Task ATrailingSlashOnTheBaseUrl_IsFine()
  {
    var (client, handler) = Make("http://cloud-billing:8080/");
    handler.Respond = () => Json(HttpStatusCode.OK, "{\"version\":\"v\",\"founding\":false,\"currency\":\"usd\",\"products\":[]}");
    await client.GetCatalogAsync(false);
    handler.Request!.RequestUri!.AbsolutePath.Should().Be("/v1/catalog");
  }
}
