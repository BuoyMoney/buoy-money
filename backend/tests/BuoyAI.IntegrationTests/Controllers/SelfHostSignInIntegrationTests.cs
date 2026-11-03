using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

/// <summary>Sign-up lock, invites and relay device registration over HTTP on a self-hosted server.</summary>
public class SelfHostSignInIntegrationTests : IClassFixture<SelfHostFactory>
{
  private readonly SelfHostFactory _factory;

  public SelfHostSignInIntegrationTests(SelfHostFactory factory) => _factory = factory;

  private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

  private static HttpRequestMessage Req(HttpMethod method, string path, string? token = null, object? body = null)
  {
    var r = new HttpRequestMessage(method, path);
    if (token != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (body != null) r.Content = Json(body);
    return r;
  }

  private static object Signup(string email, string? invite = null) =>
    new { email, password = "Str0ng!Passw0rd42", firstName = "Home", lastName = "Member", inviteCode = invite };

  private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
  {
    var text = await response.Content.ReadAsStringAsync();
    return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
  }

  internal static string PhoneKey()
  {
    using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    var q = key.ExportParameters(false).Q;
    var raw = new byte[65];
    raw[0] = 4;
    q.X!.CopyTo(raw, 1);
    q.Y!.CopyTo(raw, 33);
    return Convert.ToBase64String(raw);
  }

  [Fact]
  public async Task TheWholeSignUpLifecycle()
  {
    var dbName = $"TestDatabase_{Guid.NewGuid()}";
    var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    {
      var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BuoyAIDbContext>));
      if (descriptor != null) services.Remove(descriptor);
      // Registration opens a real transaction; the InMemory provider throws on that unless told to ignore it.
      services.AddDbContext<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName)
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
      services.AddDbContextFactory<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName)
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)), ServiceLifetime.Scoped);
    }));
    var client = factory.CreateClient();
    using (var scope = factory.Services.CreateScope())
    {
      // The shared test factory seeds one user; a fresh home server has none.
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      db.Users.RemoveRange(db.Users.ToList());
      await db.SaveChangesAsync();
    }

    // A fresh server says it is waiting for its first account.
    var status = await ReadAsync(await client.GetAsync("/api/registration/status"));
    status.GetProperty("firstAccount").GetBoolean().Should().BeTrue();
    status.GetProperty("requiresInvite").GetBoolean().Should().BeFalse();

    // The first account is the admin.
    var first = await client.SendAsync(Req(HttpMethod.Post, "/api/auth/register", body: Signup("owner@home.test")));
    first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
    var firstJson = await ReadAsync(first);
    firstJson.GetProperty("user").GetProperty("subscriptionTier").GetString().Should().Be("Admin");
    var adminToken = firstJson.GetProperty("token").GetString()!;

    // Then the door closes.
    status = await ReadAsync(await client.GetAsync("/api/registration/status"));
    status.GetProperty("firstAccount").GetBoolean().Should().BeFalse();
    status.GetProperty("requiresInvite").GetBoolean().Should().BeTrue();
    var refused = await client.SendAsync(Req(HttpMethod.Post, "/api/auth/register", body: Signup("stranger@home.test")));
    refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    (await ReadAsync(refused)).GetProperty("code").GetString().Should().Be("registration_closed");

    // The admin makes an invite. The code is in that one response.
    var created = await client.SendAsync(Req(HttpMethod.Post, "/api/registration/invites", adminToken, new { note = "partner", validDays = 3 }));
    created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
    var code = (await ReadAsync(created)).GetProperty("code").GetString()!;
    var listing = await ReadAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/registration/admin", adminToken)));
    listing.GetRawText().Should().NotContain(code, "only a hash of the code is kept");

    // The invite admits one person, as a normal member who cannot run the lock.
    var joined = await client.SendAsync(Req(HttpMethod.Post, "/api/auth/register", body: Signup("partner@home.test", code)));
    joined.StatusCode.Should().Be(HttpStatusCode.OK, await joined.Content.ReadAsStringAsync());
    var joinedJson = await ReadAsync(joined);
    joinedJson.GetProperty("user").GetProperty("subscriptionTier").GetString().Should().Be("Premium");
    var memberToken = joinedJson.GetProperty("token").GetString()!;
    (await client.SendAsync(Req(HttpMethod.Post, "/api/auth/register", body: Signup("third@home.test", code))))
      .StatusCode.Should().Be(HttpStatusCode.Forbidden, "an invite is single use");
    (await client.SendAsync(Req(HttpMethod.Put, "/api/registration/open", memberToken, new { open = true })))
      .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    (await client.SendAsync(Req(HttpMethod.Post, "/api/registration/invites", memberToken, new { })))
      .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    // Opening registration lets anyone in; closing it again shuts the door.
    (await client.SendAsync(Req(HttpMethod.Put, "/api/registration/open", adminToken, new { open = true })))
      .StatusCode.Should().Be(HttpStatusCode.OK);
    (await client.SendAsync(Req(HttpMethod.Post, "/api/auth/register", body: Signup("guest@home.test"))))
      .StatusCode.Should().Be(HttpStatusCode.OK);
    (await client.SendAsync(Req(HttpMethod.Put, "/api/registration/open", adminToken, new { open = false })))
      .StatusCode.Should().Be(HttpStatusCode.OK);
    (await client.SendAsync(Req(HttpMethod.Post, "/api/auth/register", body: Signup("late@home.test"))))
      .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    // Relay devices must bring a real public key.
    (await client.SendAsync(Req(HttpMethod.Post, "/api/notifications/devices", memberToken, new { token = "pair-1", platform = "relay" })))
      .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    (await client.SendAsync(Req(HttpMethod.Post, "/api/notifications/devices", memberToken, new { token = "pair-1", platform = "relay", publicKey = PhoneKey() })))
      .StatusCode.Should().Be(HttpStatusCode.NoContent);
  }

  [Fact]
  public async Task AStrangerCannotBecomeAdmin_AndAppleLoginIsOffOnSelfHost()
  {
    var dbName = $"TestDatabase_{Guid.NewGuid()}";
    var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    {
      var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BuoyAIDbContext>));
      if (descriptor != null) services.Remove(descriptor);
      services.AddDbContext<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName)
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
      services.AddDbContextFactory<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName)
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)), ServiceLifetime.Scoped);
    }));
    var client = factory.CreateClient();
    using (var scope = factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      db.Users.RemoveRange(db.Users.ToList());
      await db.SaveChangesAsync();
    }

    // The factory lists owner@home.test in Admin:Emails, so a stranger arriving first is refused.
    (await client.SendAsync(Req(HttpMethod.Post, "/api/auth/register", body: Signup("stranger@home.test"))))
      .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    (await client.SendAsync(Req(HttpMethod.Post, "/api/auth/register", body: Signup("owner@home.test"))))
      .StatusCode.Should().Be(HttpStatusCode.OK);

    (await client.SendAsync(Req(HttpMethod.Post, "/api/auth/apple-login", body: new { identityToken = "x", userIdentifier = "y" })))
      .StatusCode.Should().Be(HttpStatusCode.NotFound);
  }

  [Fact]
  public async Task Version_ReportsNoRelayUntilOneIsConfigured()
  {
    var json = await ReadAsync(await _factory.CreateClient().GetAsync("/api/system/version"));
    json.TryGetProperty("pushRelayUrl", out var relay).Should().BeTrue();
    relay.ValueKind.Should().Be(JsonValueKind.Null);
    var features = json.GetProperty("features");
    features.GetProperty("googleSignIn").GetBoolean().Should().BeFalse("no Google client is configured");
    features.GetProperty("email").GetBoolean().Should().BeFalse("no SMTP host is configured");
  }
}

public class HostedSignUpIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
  private readonly TestWebApplicationFactory<Program> _factory;

  public HostedSignUpIntegrationTests(TestWebApplicationFactory<Program> factory) => _factory = factory;

  [Fact]
  public async Task Hosted_ReportsEveryFeatureOn_AndNoRelay()
  {
    var response = await _factory.CreateClient().GetAsync("/api/system/version");
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var root = json.RootElement;
    root.GetProperty("edition").GetString().Should().Be("hosted");
    root.GetProperty("pushRelayUrl").ValueKind.Should().Be(JsonValueKind.Null);
    foreach (var name in new[] { "googleSignIn", "passkeys", "email" })
      root.GetProperty("features").GetProperty(name).GetBoolean().Should().BeTrue(name);
  }

  [Fact]
  public async Task Hosted_RefusesRelayDevices()
  {
    var client = _factory.CreateClient();
    var login = await client.PostAsync("/api/auth/login", new StringContent(
      JsonSerializer.Serialize(new { email = "test@BuoyAI.com", password = "password123" }), Encoding.UTF8, "application/json"));
    login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
    var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

    var request = new HttpRequestMessage(HttpMethod.Post, "/api/notifications/devices")
    {
      Content = new StringContent(
        JsonSerializer.Serialize(new { token = "pair-1", platform = "relay", publicKey = SelfHostSignInIntegrationTests.PhoneKey() }),
        Encoding.UTF8, "application/json")
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
  }

  [Fact]
  public async Task TheSignUpLockDoesNotExistOnHosted()
  {
    var client = _factory.CreateClient();

    (await client.GetAsync("/api/registration/status")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    var invites = new HttpRequestMessage(HttpMethod.Post, "/api/registration/invites") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
    (await client.SendAsync(invites)).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
  }
}
