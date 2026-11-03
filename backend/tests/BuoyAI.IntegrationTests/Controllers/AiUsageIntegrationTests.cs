using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services.Ai;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

internal static class AiUsageTestHelpers
{
  public static async Task SeedUserAsync(IServiceProvider services, string email)
  {
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    if (db.Users.Any(u => u.Email == email)) return; // the factory (and its database) is shared by the tests of a class
    db.Users.Add(new User
    {
      Email = email,
      Username = email.Split('@')[0],
      FirstName = "Home",
      LastName = "Owner",
      PasswordHash = BuoyAI.Infrastructure.Services.AuthService.HashPassword("Str0ng!Passw0rd42"),
      Currency = "USD",
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      IsTrialActive = false,
      TrialEndDate = DateTime.UtcNow.AddDays(-40),
      HasUsedTrial = true,
    });
    await db.SaveChangesAsync();
  }

  public static async Task<string> LoginAsync(HttpClient client, string email)
  {
    var response = await client.PostAsync("/api/auth/login", new StringContent(
      JsonSerializer.Serialize(new { email, password = "Str0ng!Passw0rd42" }), Encoding.UTF8, "application/json"));
    response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return doc.RootElement.GetProperty("token").GetString()!;
  }

  public static HttpRequestMessage Request(HttpMethod method, string path, string? token, object? body = null)
  {
    var request = new HttpRequestMessage(method, path);
    if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    return request;
  }
}

/// <summary>A self-hosted server on its own model or key: AI usage is statistics, nothing caps it, and pay-as-you-go does not exist.</summary>
public class AiUsageSelfHostIntegrationTests : IClassFixture<SelfHostFactory>
{
  private readonly SelfHostFactory _factory;

  public AiUsageSelfHostIntegrationTests(SelfHostFactory factory) => _factory = factory;

  [Fact]
  public async Task TheUsageScreen_NeedsASignIn()
  {
    (await _factory.CreateClient().GetAsync("/api/aiusage")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
  }

  [Fact]
  public async Task Unlimited_LocalStatistics_ForAnyMember()
  {
    var client = _factory.CreateClient();
    await AiUsageTestHelpers.SeedUserAsync(_factory.Services, "usage-member@home.test");
    var token = await AiUsageTestHelpers.LoginAsync(client, "usage-member@home.test");

    var response = await client.SendAsync(AiUsageTestHelpers.Request(HttpMethod.Get, "/api/aiusage", token));

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    json.RootElement.GetProperty("source").GetString().Should().Be("local");
    json.RootElement.GetProperty("unlimited").GetBoolean().Should().BeTrue();
    json.RootElement.GetProperty("blocked").GetBoolean().Should().BeFalse();
    json.RootElement.GetProperty("payg").GetProperty("available").GetBoolean().Should().BeFalse();
  }

  [Fact]
  public async Task AMemberCannotChangePayAsYouGo()
  {
    var client = _factory.CreateClient();
    await AiUsageTestHelpers.SeedUserAsync(_factory.Services, "usage-member2@home.test");
    var token = await AiUsageTestHelpers.LoginAsync(client, "usage-member2@home.test");

    var response = await client.SendAsync(AiUsageTestHelpers.Request(HttpMethod.Put, "/api/aiusage/payg", token, new { enabled = true, capMicros = 5_000_000 }));

    response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
  }

  [Fact]
  public async Task TheAdmin_WithoutBuoyCloud_GetsAPlainRefusal()
  {
    var client = _factory.CreateClient();
    await AiUsageTestHelpers.SeedUserAsync(_factory.Services, "owner@home.test");
    var token = await AiUsageTestHelpers.LoginAsync(client, "owner@home.test");

    var response = await client.SendAsync(AiUsageTestHelpers.Request(HttpMethod.Put, "/api/aiusage/payg", token, new { enabled = true, capMicros = 5_000_000 }));

    response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    (await response.Content.ReadAsStringAsync()).Should().Contain("BuoyAI Cloud");
  }
}

public sealed class SelfHostCloudFactory : TestWebApplicationFactory<Program>
{
  public List<(string Method, string Path, string Body, string? Auth)> Seen { get; } = new();

  public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK)
  {
    Content = new StringContent("""
      {"periodStart":"2026-10-01T00:00:00Z","periodEnd":"2026-11-01T00:00:00Z","allowanceMicros":1500000,"usedMicros":1200000,"percent":80,
       "state":"warning","blocked":false,"projectedMicros":1800000,
       "payg":{"available":true,"enabled":false,"capMicros":0,"usedMicros":0,"maxCapMicros":20000000},
       "byFeature":[{"feature":"categorization","calls":40,"costMicros":1200000}]}
      """, Encoding.UTF8, "application/json"),
  };

  private sealed class Handler(SelfHostCloudFactory owner) : HttpMessageHandler
  {
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
      owner.Seen.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString()));
      return owner.Respond(request);
    }
  }

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseSetting("BuoyMoney:Edition", "selfhost");
    builder.UseSetting("Ai:Provider", "buoy-cloud");
    builder.UseSetting("Ai:CloudUrl", "https://cloud.test");
    builder.UseSetting("Ai:CloudCredential", "pair-secret-123");
    base.ConfigureWebHost(builder);
    builder.ConfigureAppConfiguration((_, config) =>
      config.AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:Emails:0"] = "owner@home.test" }));
    builder.ConfigureTestServices(services =>
    {
      foreach (var d in services.Where(d => d.ServiceType == typeof(CloudAiGatewayClient)).ToList()) services.Remove(d);
      services.AddSingleton(sp => new CloudAiGatewayClient(new HttpClient(new Handler(this)), sp.GetRequiredService<AiProviderOptions>()));
    });
  }
}

/// <summary>A self-hosted server whose AI comes from BuoyAI Cloud: the usage screen shows the gateway's allowance.</summary>
public class AiUsageCloudIntegrationTests : IClassFixture<SelfHostCloudFactory>
{
  private readonly SelfHostCloudFactory _factory;

  public AiUsageCloudIntegrationTests(SelfHostCloudFactory factory) => _factory = factory;

  [Fact]
  public async Task TheSettingsScreen_SaysBuoyCloudIsTheProvider()
  {
    var client = _factory.CreateClient();
    await AiUsageTestHelpers.SeedUserAsync(_factory.Services, "cloud-member0@home.test");
    var token = await AiUsageTestHelpers.LoginAsync(client, "cloud-member0@home.test");

    var response = await client.SendAsync(AiUsageTestHelpers.Request(HttpMethod.Get, "/api/aisettings", token));

    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    json.RootElement.GetProperty("provider").GetString().Should().Be("buoy-cloud");
    json.RootElement.GetProperty("enabled").GetBoolean().Should().BeTrue();
    var features = json.RootElement.GetProperty("features").EnumerateArray().ToDictionary(f => f.GetProperty("id").GetString()!, f => f.GetProperty("status").GetString());
    features["categorization"].Should().Be("works");
    features["chat"].Should().Be("off", "chat with tools still runs only on Gemini");
  }

  [Fact]
  public async Task AnyMember_SeesTheGatewaysAllowance()
  {
    _factory.Seen.Clear();
    var client = _factory.CreateClient();
    await AiUsageTestHelpers.SeedUserAsync(_factory.Services, "cloud-member@home.test");
    var token = await AiUsageTestHelpers.LoginAsync(client, "cloud-member@home.test");

    var response = await client.SendAsync(AiUsageTestHelpers.Request(HttpMethod.Get, "/api/aiusage", token));

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    json.RootElement.GetProperty("source").GetString().Should().Be("cloud");
    json.RootElement.GetProperty("allowanceMicros").GetInt64().Should().Be(1_500_000);
    json.RootElement.GetProperty("percent").GetInt32().Should().Be(80);
    json.RootElement.GetProperty("payg").GetProperty("available").GetBoolean().Should().BeTrue();
    json.RootElement.GetProperty("byFeature")[0].GetProperty("feature").GetString().Should().Be("categorization");
    _factory.Seen.Should().ContainSingle().Which.Should().Be(("GET", "/v1/usage", "", "Bearer pair-secret-123"));
  }

  [Fact]
  public async Task OnlyTheAdmin_CanTurnOnPayAsYouGo_AndTheChoiceReachesTheGateway()
  {
    var client = _factory.CreateClient();
    await AiUsageTestHelpers.SeedUserAsync(_factory.Services, "cloud-member2@home.test");
    await AiUsageTestHelpers.SeedUserAsync(_factory.Services, "owner@home.test");
    var memberToken = await AiUsageTestHelpers.LoginAsync(client, "cloud-member2@home.test");
    var adminToken = await AiUsageTestHelpers.LoginAsync(client, "owner@home.test");
    _factory.Seen.Clear();

    var refused = await client.SendAsync(AiUsageTestHelpers.Request(HttpMethod.Put, "/api/aiusage/payg", memberToken, new { enabled = true, capMicros = 5_000_000 }));
    refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    _factory.Seen.Should().BeEmpty("a member's request never reaches the gateway");

    var allowed = await client.SendAsync(AiUsageTestHelpers.Request(HttpMethod.Put, "/api/aiusage/payg", adminToken, new { enabled = true, capMicros = 5_000_000 }));
    allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    var call = _factory.Seen.Should().ContainSingle().Subject;
    call.Method.Should().Be("PUT");
    call.Path.Should().Be("/v1/usage/payg");
    using var sent = JsonDocument.Parse(call.Body);
    sent.RootElement.GetProperty("enabled").GetBoolean().Should().BeTrue();
    sent.RootElement.GetProperty("capMicros").GetInt64().Should().Be(5_000_000);
  }

  [Fact]
  public async Task AGatewayRefusalOfTheCap_ComesBackAsAPlain400()
  {
    var client = _factory.CreateClient();
    await AiUsageTestHelpers.SeedUserAsync(_factory.Services, "owner@home.test");
    var adminToken = await AiUsageTestHelpers.LoginAsync(client, "owner@home.test");
    var original = _factory.Respond;
    _factory.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
    {
      Content = new StringContent("""{"error":{"code":"bad_cap","message":"Set a limit above $0 and at most $20."}}""", Encoding.UTF8, "application/json"),
    };
    try
    {
      var response = await client.SendAsync(AiUsageTestHelpers.Request(HttpMethod.Put, "/api/aiusage/payg", adminToken, new { enabled = true, capMicros = 0 }));

      response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
      (await response.Content.ReadAsStringAsync()).Should().Contain("Set a limit above $0");
    }
    finally
    {
      _factory.Respond = original;
    }
  }
}
