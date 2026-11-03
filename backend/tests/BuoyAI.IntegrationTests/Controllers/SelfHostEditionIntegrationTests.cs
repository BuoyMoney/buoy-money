using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

/// <summary>The whole application started as a self-hosted server, in-process.</summary>
public sealed class SelfHostFactory : TestWebApplicationFactory<Program>
{
  public string WebRoot { get; } = Path.Combine(Path.GetTempPath(), "bm-selfhost-" + Guid.NewGuid().ToString("N"));

  public SelfHostFactory()
  {
    Directory.CreateDirectory(WebRoot);
    File.WriteAllText(Path.Combine(WebRoot, "index.html"), "<html>buoy money</html>");
  }

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    // UseSetting reaches the host configuration before Program.cs reads the edition.
    builder.UseSetting("BuoyMoney:Edition", "selfhost");
    base.ConfigureWebHost(builder);
    builder.ConfigureAppConfiguration((_, config) =>
      config.AddInMemoryCollection(new Dictionary<string, string?> { ["SelfHost:WebRoot"] = WebRoot, ["Admin:Emails:0"] = "owner@home.test" }));
  }

  protected override void Dispose(bool disposing)
  {
    base.Dispose(disposing);
    if (disposing && Directory.Exists(WebRoot)) Directory.Delete(WebRoot, true);
  }
}

public class SelfHostEditionIntegrationTests : IClassFixture<SelfHostFactory>
{
  private readonly SelfHostFactory _factory;

  public SelfHostEditionIntegrationTests(SelfHostFactory factory) => _factory = factory;

  private HttpClient Client() => _factory.CreateClient(new() { AllowAutoRedirect = false });

  [Fact]
  public async Task Version_SaysSelfhost()
  {
    var response = await Client().GetAsync("/api/system/version");

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    json.RootElement.GetProperty("edition").GetString().Should().Be("selfhost");
    json.RootElement.GetProperty("version").GetString().Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    json.RootElement.GetProperty("minClientVersion").GetString().Should().NotBeNullOrEmpty();
  }

  [Theory]
  [InlineData("GET", "/api/billing/current")]
  [InlineData("POST", "/api/billing/webhook")]
  [InlineData("POST", "/api/billing/revenuecat/webhook")]
  [InlineData("GET", "/api/plaid/connections")]
  [InlineData("POST", "/api/plaid/webhook/receive")]
  [InlineData("GET", "/api/referral")]
  [InlineData("POST", "/api/leads")]
  public async Task TheHostedOnlyRoutes_AreNotFound_EvenWithoutSignIn(string method, string path)
  {
    var response = await Client().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

    response.StatusCode.Should().Be(HttpStatusCode.NotFound, "a hosted-only route must look like it does not exist, not ask for a sign-in");
  }

  [Fact]
  public async Task TheHouseholdRoutes_StillWork()
  {
    // Signed out, a protected household route asks for credentials: it exists.
    var response = await Client().GetAsync("/api/user/profile");

    response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
  }

  [Fact]
  public async Task TheWebAppIsServedByTheSameProcess()
  {
    var root = await Client().GetAsync("/");
    root.StatusCode.Should().Be(HttpStatusCode.Redirect);
    root.Headers.Location!.OriginalString.Should().Be("/app/");

    var app = await Client().GetAsync("/app/");
    app.StatusCode.Should().Be(HttpStatusCode.OK);
    (await app.Content.ReadAsStringAsync()).Should().Contain("buoy money");
    app.Headers.Contains("Content-Security-Policy").Should().BeTrue();

    (await Client().GetAsync("/app/budgets/42")).StatusCode.Should().Be(HttpStatusCode.OK);
  }

  private async Task SeedUserAsync(string email)
  {
    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
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
      // A lapsed trial and no stored tier: what a self-host account looks like after 30 days.
      IsTrialActive = false,
      TrialEndDate = DateTime.UtcNow.AddDays(-40),
      HasUsedTrial = true,
    });
    await db.SaveChangesAsync();
  }

  private async Task<(string Token, JsonElement User)> LoginAsync(string email)
  {
    var response = await Client().PostAsync("/api/auth/login", new StringContent(
      JsonSerializer.Serialize(new { email, password = "Str0ng!Passw0rd42" }), Encoding.UTF8, "application/json"));
    response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return (doc.RootElement.GetProperty("token").GetString()!, doc.RootElement.GetProperty("user").Clone());
  }

  [Fact]
  public async Task TheConfiguredAdmin_CanCallAdminRoutes()
  {
    await SeedUserAsync("owner@home.test");
    var (token, user) = await LoginAsync("owner@home.test");
    user.GetProperty("subscriptionTier").GetString().Should().Be("Admin");

    var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/overview");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var response = await Client().SendAsync(request);

    response.StatusCode.Should().Be(HttpStatusCode.OK, "the JWT must carry the Admin tier on self-host");
  }

  [Fact]
  public async Task AnOrdinaryAccount_IsPremium_EvenAfterTheTrialLapsed()
  {
    await SeedUserAsync("kid@home.test");

    var (token, user) = await LoginAsync("kid@home.test");

    user.GetProperty("subscriptionTier").GetString().Should().Be("Premium");
    var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/overview");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    (await Client().SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
  }

  [Fact]
  public async Task AiSettings_NeedASignIn()
  {
    (await Client().GetAsync("/api/aisettings")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
  }

  private async Task<JsonDocument> GetAiSettingsAsync(string email)
  {
    var (token, _) = await LoginAsync(email);
    var request = new HttpRequestMessage(HttpMethod.Get, "/api/aisettings");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var response = await Client().SendAsync(request);
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
  }

  [Fact]
  public async Task SelfHost_AiIsOffByDefault_AndEveryFeatureSaysSo()
  {
    await SeedUserAsync("member@home.test");

    using var json = await GetAiSettingsAsync("member@home.test");

    json.RootElement.GetProperty("enabled").GetBoolean().Should().BeFalse();
    json.RootElement.GetProperty("provider").GetString().Should().Be("none");
    json.RootElement.GetProperty("edition").GetString().Should().Be("selfhost");
    var features = json.RootElement.GetProperty("features").EnumerateArray().ToList();
    features.Should().NotBeEmpty();
    features.Should().OnlyContain(f => f.GetProperty("status").GetString() == "off");
    features.Select(f => f.GetProperty("id").GetString()).Should().Contain(new[] { "categorization", "chat", "insights" });
    // An ordinary member gets no connection details.
    json.RootElement.GetProperty("details").ValueKind.Should().Be(JsonValueKind.Null);
  }

  [Fact]
  public async Task SelfHost_TheAdminSeesConnectionDetails_AndTheCheckSaysAiIsOff()
  {
    await SeedUserAsync("owner@home.test");

    using var json = await GetAiSettingsAsync("owner@home.test");
    json.RootElement.GetProperty("details").ValueKind.Should().Be(JsonValueKind.Object);
    json.RootElement.GetProperty("details").GetProperty("apiKeySet").GetBoolean().Should().BeFalse();

    var (token, _) = await LoginAsync("owner@home.test");
    var probe = new HttpRequestMessage(HttpMethod.Post, "/api/aisettings/probe");
    probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var response = await Client().SendAsync(probe);
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
    result.RootElement.GetProperty("steps").GetArrayLength().Should().Be(1);
  }

  [Fact]
  public async Task SelfHost_AMemberCannotRunTheCheck()
  {
    await SeedUserAsync("member2@home.test");
    var (token, _) = await LoginAsync("member2@home.test");
    var probe = new HttpRequestMessage(HttpMethod.Post, "/api/aisettings/probe");
    probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    (await Client().SendAsync(probe)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
  }

  [Fact]
  public async Task Liveness_IsAnswered()
  {
    (await Client().GetAsync("/api/alive")).StatusCode.Should().Be(HttpStatusCode.OK);
  }
}

/// <summary>The default (hosted) application must not have changed shape.</summary>
public class HostedEditionIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
  private readonly TestWebApplicationFactory<Program> _factory;

  public HostedEditionIntegrationTests(TestWebApplicationFactory<Program> factory) => _factory = factory;

  [Fact]
  public async Task Version_SaysHosted_AndAcceptsEveryClient()
  {
    var response = await _factory.CreateClient().GetAsync("/api/system/version");

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    json.RootElement.GetProperty("edition").GetString().Should().Be("hosted");
    json.RootElement.GetProperty("minClientVersion").GetString().Should().Be("1.0.0");
  }

  [Fact]
  public async Task TheHostedRoutes_AreStillThere()
  {
    var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

    // Protected routes answer 401 signed out; the webhook endpoints answer from their own handler.
    (await client.GetAsync("/api/plaid/connections")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    (await client.GetAsync("/api/billing/current")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    (await client.PostAsync("/api/billing/webhook", new StringContent("{}"))).StatusCode.Should().NotBe(HttpStatusCode.NotFound);
  }

  [Fact]
  public async Task TheHostedAppDoesNotServeTheWebAppItself()
  {
    var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

    (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.NotFound, "nginx serves the site and the app in hosted");
    (await client.GetAsync("/app/")).StatusCode.Should().Be(HttpStatusCode.NotFound);
  }
}
