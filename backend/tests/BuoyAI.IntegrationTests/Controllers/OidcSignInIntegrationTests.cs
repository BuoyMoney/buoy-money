using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services.Oidc;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

/// <summary>Single sign-on over HTTP: hosted hides it, self-host reports it, and a full round trip signs in.</summary>
public class OidcSignInIntegrationTests : IClassFixture<SelfHostFactory>, IClassFixture<TestWebApplicationFactory<Program>>
{
  private const string Issuer = "https://id.home.test";
  private const string Origin = "https://buoy.home.test";

  private readonly SelfHostFactory _selfHost;
  private readonly TestWebApplicationFactory<Program> _hosted;

  public OidcSignInIntegrationTests(SelfHostFactory selfHost, TestWebApplicationFactory<Program> hosted)
  {
    _selfHost = selfHost;
    _hosted = hosted;
  }

  private static Dictionary<string, string?> OidcSettings(bool disablePasswords = false) => new()
  {
    ["App:BaseUrl"] = Origin,
    ["Oidc:Enabled"] = "true",
    ["Oidc:Authority"] = Issuer,
    ["Oidc:ClientId"] = "buoy-money",
    ["Oidc:ClientSecret"] = "provider-secret",
    ["Oidc:DisplayName"] = "Pocket ID",
    ["Oidc:DisablePasswordLogin"] = disablePasswords ? "true" : "false",
  };

  private WebApplicationFactory<Program> Configured(Dictionary<string, string?> settings, Provider? provider = null)
  {
    var dbName = $"TestDatabase_{Guid.NewGuid()}";
    return _selfHost.WithWebHostBuilder(builder =>
    {
      builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
      builder.ConfigureServices(services =>
      {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BuoyAIDbContext>));
        if (descriptor != null) services.Remove(descriptor);
        services.AddDbContext<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName)
          .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContextFactory<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName)
          .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)), ServiceLifetime.Scoped);
        if (provider != null)
        {
          services.AddHttpClient(OidcProviderClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => provider)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        }
      });
    });
  }

  private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
    JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

  [Theory]
  [InlineData("GET", "/api/auth/oidc/start?platform=web")]
  [InlineData("GET", "/api/auth/oidc/callback?code=x&state=y")]
  [InlineData("POST", "/api/auth/oidc/exchange")]
  public async Task Hosted_TheSingleSignOnRoutesAreNotFound(string method, string path)
  {
    var client = _hosted.CreateClient(new() { AllowAutoRedirect = false });
    var request = new HttpRequestMessage(new HttpMethod(method), path);
    if (method == "POST") request.Content = new StringContent("{\"code\":\"x\"}", Encoding.UTF8, "application/json");

    var response = await client.SendAsync(request);

    response.StatusCode.Should().Be(HttpStatusCode.NotFound);
  }

  [Fact]
  public async Task Hosted_VersionDoesNotOfferSingleSignOn()
  {
    var json = await ReadAsync(await _hosted.CreateClient().GetAsync("/api/system/version"));

    json.GetProperty("features").GetProperty("oidc").GetBoolean().Should().BeFalse();
    json.GetProperty("features").GetProperty("passwordLogin").GetBoolean().Should().BeTrue();
    json.GetProperty("oidcDisplayName").ValueKind.Should().Be(JsonValueKind.Null);
  }

  [Fact]
  public async Task SelfHost_NotSetUp_RoutesAreNotFound_AndVersionSaysOff()
  {
    var client = _selfHost.CreateClient(new() { AllowAutoRedirect = false });

    (await client.GetAsync("/api/auth/oidc/start")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    var json = await ReadAsync(await client.GetAsync("/api/system/version"));
    json.GetProperty("features").GetProperty("oidc").GetBoolean().Should().BeFalse();
  }

  [Fact]
  public async Task SelfHost_SetUp_VersionOffersTheButton()
  {
    var client = Configured(OidcSettings()).CreateClient();

    var json = await ReadAsync(await client.GetAsync("/api/system/version"));

    json.GetProperty("features").GetProperty("oidc").GetBoolean().Should().BeTrue();
    json.GetProperty("features").GetProperty("passwordLogin").GetBoolean().Should().BeTrue();
    json.GetProperty("oidcDisplayName").GetString().Should().Be("Pocket ID");
  }

  [Fact]
  public async Task SelfHost_PasswordsTurnedOff_PasswordLoginIsRefusedWith403()
  {
    var client = Configured(OidcSettings(disablePasswords: true)).CreateClient();

    var version = await ReadAsync(await client.GetAsync("/api/system/version"));
    var login = await client.PostAsync("/api/auth/login",
      new StringContent("{\"email\":\"test@buoyai.com\",\"password\":\"password123\"}", Encoding.UTF8, "application/json"));

    version.GetProperty("features").GetProperty("passwordLogin").GetBoolean().Should().BeFalse();
    login.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    (await ReadAsync(login)).GetProperty("code").GetString().Should().Be("password_login_disabled");
  }

  [Fact]
  public async Task SelfHost_PasswordsTurnedOff_ButSingleSignOnNotEnabled_PasswordsStillWork()
  {
    var settings = OidcSettings(disablePasswords: true);
    settings["Oidc:Enabled"] = "false";
    var client = Configured(settings).CreateClient();

    var login = await client.PostAsync("/api/auth/login",
      new StringContent("{\"email\":\"test@buoyai.com\",\"password\":\"wrong-password\"}", Encoding.UTF8, "application/json"));

    login.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a broken single sign-on setup must never lock out passwords");
  }

  [Fact]
  public async Task SelfHost_FullRoundTrip_SignsAnExistingUserIn()
  {
    using var provider = new Provider("test@buoyai.com");
    var factory = Configured(OidcSettings(), provider);
    var client = factory.CreateClient(new() { AllowAutoRedirect = false });
    using (var scope = factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      db.Users.Add(new BuoyAI.Core.Entities.User
      {
        Username = "sso", Email = "test@buoyai.com", PasswordHash = "", FirstName = "Sso", LastName = "User",
        IsEmailVerified = true, Currency = "USD", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
      });
      await db.SaveChangesAsync();
    }

    var start = await client.GetAsync("/api/auth/oidc/start?platform=web");
    start.StatusCode.Should().Be(HttpStatusCode.Redirect);
    var authorize = start.Headers.Location!;
    authorize.ToString().Should().StartWith($"{Issuer}/authorize?");
    var query = Parse(authorize.Query);
    provider.Nonce = query["nonce"];

    var callback = await client.GetAsync($"/api/auth/oidc/callback?code=provider-code&state={Uri.EscapeDataString(query["state"])}");
    callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
    var back = callback.Headers.Location!;
    back.ToString().Should().StartWith($"{Origin}/app/auth/oidc?code=");
    back.ToString().Should().NotContain("token");
    var code = Parse(back.Query)["code"];

    var exchange = await client.PostAsync("/api/auth/oidc/exchange",
      new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json"));
    exchange.StatusCode.Should().Be(HttpStatusCode.OK);
    var json = await ReadAsync(exchange);
    json.GetProperty("requiresMfa").GetBoolean().Should().BeFalse();
    json.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
    json.GetProperty("user").GetProperty("email").GetString().Should().BeEquivalentTo("test@buoyai.com");

    var replay = await client.PostAsync("/api/auth/oidc/exchange",
      new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json"));
    replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
  }

  private static Dictionary<string, string> Parse(string query) =>
    query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
      .Select(p => p.Split('=', 2))
      .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");

  /// <summary>A minimal OpenID Connect provider: discovery, JWKS and a token endpoint signing with a real RSA key.</summary>
  public sealed class Provider : HttpMessageHandler
  {
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly string _email;

    public Provider(string email) => _email = email;

    public string Nonce { get; set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      await Task.Yield();
      var path = request.RequestUri!.AbsolutePath;
      object? body = path switch
      {
        "/.well-known/openid-configuration" => new
        {
          issuer = Issuer,
          authorization_endpoint = $"{Issuer}/authorize",
          token_endpoint = $"{Issuer}/token",
          jwks_uri = $"{Issuer}/jwks",
        },
        "/jwks" => Jwks(),
        "/token" => new { access_token = "at", token_type = "Bearer", id_token = IdToken() },
        _ => null,
      };
      if (body == null) return new HttpResponseMessage(HttpStatusCode.NotFound);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
      };
    }

    private object Jwks()
    {
      var p = _rsa.ExportParameters(false);
      return new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "k1", n = B64(p.Modulus!), e = B64(p.Exponent!) } } };
    }

    private string IdToken()
    {
      var key = new RsaSecurityKey(_rsa) { KeyId = "k1" };
      var token = new JwtSecurityToken(
        issuer: Issuer,
        audience: "buoy-money",
        claims: new[]
        {
          new Claim("sub", "pocket-1"),
          new Claim("nonce", Nonce),
          new Claim("email", _email),
          new Claim("email_verified", "true", ClaimValueTypes.Boolean),
        },
        notBefore: DateTime.UtcNow.AddMinutes(-1),
        expires: DateTime.UtcNow.AddMinutes(5),
        signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
      return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    protected override void Dispose(bool disposing)
    {
      if (disposing) _rsa.Dispose();
      base.Dispose(disposing);
    }
  }
}
