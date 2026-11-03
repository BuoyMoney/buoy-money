using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs.OAuth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// BuoyAI Connect OAuth 2.1 authorization server (RFC #782): full authorize→code→token flow, PKCE,
/// single-use code reuse, rotating refresh tokens + reuse detection, CIMD metadata validation,
/// resource pinning, and the config-declared client upsert. Backed by an in-memory DbContext shared
/// across the factory-created contexts the service uses.
/// </summary>
public class OAuthConnectServiceTests : IDisposable
{
  private const string ClientId = "test-client";
  private const string RedirectUri = "https://client.example/callback";
  private const string Issuer = "https://test.buoy-ai.com";
  private const string ResourceUri = "https://test.buoy-ai.com/mcp";

  private readonly BuoyAIDbContext _context;
  private readonly McpOptions _options;
  private readonly Mock<IEntitlementService> _billing = new();
  private Func<HttpRequestMessage, HttpResponseMessage> _cimdResponder;
  // DNS seam for the SSRF guard. Defaults to a public IP so CIMD tests (which use the reserved
  // client.example host) resolve; SSRF tests override it to return a private/internal address.
  private Func<string, CancellationToken, Task<IPAddress[]>> _hostResolver =
    (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
  private readonly OAuthConnectService _service;

  public OAuthConnectServiceTests()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;
    _context = new BuoyAIDbContext(options);

    _options = new McpOptions { Enabled = true, Issuer = Issuer, ResourceUri = ResourceUri };
    _billing.Setup(b => b.GetEffectiveTierAsync(It.IsAny<int>())).ReturnsAsync(SubscriptionTier.Premium);

    // Default: no CIMD host reachable (404). CIMD tests overwrite _cimdResponder before acting.
    _cimdResponder = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    var httpFactory = new Mock<IHttpClientFactory>();
    httpFactory
      .Setup(f => f.CreateClient(It.IsAny<string>()))
      .Returns(() => new HttpClient(new StubHttpMessageHandler(req => _cimdResponder(req))));

    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["Jwt:Key"] = "unit-test-jwt-key-0123456789abcdef0123456789abcdef",
      })
      .Build();

    _service = new OAuthConnectService(
      new SharedOptionsDbContextFactory(options),
      httpFactory.Object,
      _billing.Object,
      Options.Create(_options),
      config,
      NullLogger<OAuthConnectService>.Instance,
      // Route DNS through the mutable seam so individual tests can steer resolution.
      (host, ct) => _hostResolver(host, ct));
  }

  public void Dispose()
  {
    _context.Database.EnsureDeleted();
    _context.Dispose();
    GC.SuppressFinalize(this);
  }

  // ------------------------------------------------------------------ happy path

  [Fact]
  public async Task FullFlow_PreRegisteredClient_IssuesAccessAndRefreshTokens()
  {
    SeedClient();
    var (verifier, challenge) = Pkce();

    var authorize = await _service.HandleAuthorizeAsync(ValidAuthorizeInput(challenge, state: "xyz"), default);
    authorize.Kind.Should().Be(AuthorizeOutcomeKind.RedirectToConsent);
    var requestId = RequestIdFromConsentUrl(authorize.RedirectUrl!);

    var view = await _service.GetConsentViewAsync(requestId, default);
    view.Should().NotBeNull();
    view!.Scopes.Should().Equal("buoyai:read", "buoyai:write:memory");

    var decision = await _service.DecideAsync(requestId, userId: 42, approve: true, default);
    decision.Should().NotBeNull();
    var query = Query(decision!.RedirectUrl);
    query["state"].Should().Be("xyz");
    var code = query["code"];

    var token = await _service.ExchangeTokenAsync(AuthCodeInput(code, verifier), default);

    token.Success.Should().BeTrue();
    token.Token!.AccessToken.Should().NotBeNullOrEmpty();
    token.Token.RefreshToken.Should().NotBeNullOrEmpty();
    token.Token.Scope.Should().Be("buoyai:read buoyai:write:memory");
    token.Token.ExpiresIn.Should().Be(3600);

    var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.Token.AccessToken);
    jwt.Issuer.Should().Be(Issuer);
    jwt.Audiences.Should().Contain(ResourceUri);
    jwt.Claims.Should().Contain(c => c.Type == "scope" && c.Value == "buoyai:read buoyai:write:memory");
    jwt.Claims.Should().Contain(c => c.Type == "client_id" && c.Value == ClientId);
    jwt.Claims.Should().Contain(c => c.Type == "subscription_tier" && c.Value == "Premium");
  }

  [Fact]
  public async Task ExchangeToken_WrongPkceVerifier_ReturnsInvalidGrant()
  {
    SeedClient();
    var (_, challenge) = Pkce();
    var requestId = await ApproveAndGetRequestId(challenge, userId: 42);
    var code = Query((await _service.DecideAsync(requestId, 42, true, default))!.RedirectUrl)["code"];

    // A different verifier than the one whose challenge was registered.
    var (otherVerifier, _) = Pkce();
    var token = await _service.ExchangeTokenAsync(AuthCodeInput(code, otherVerifier), default);

    token.Success.Should().BeFalse();
    token.StatusCode.Should().Be(400);
    token.Error.Should().Be("invalid_grant");
  }

  // ------------------------------------------------------------------ code reuse

  [Fact]
  public async Task ExchangeToken_ReusedAuthorizationCode_RevokesGrantRefreshTokens()
  {
    SeedClient();
    var (verifier, challenge) = Pkce();
    var requestId = await ApproveAndGetRequestId(challenge, userId: 42);
    var code = Query((await _service.DecideAsync(requestId, 42, true, default))!.RedirectUrl)["code"];

    var first = await _service.ExchangeTokenAsync(AuthCodeInput(code, verifier), default);
    first.Success.Should().BeTrue();
    var issuedRefresh = first.Token!.RefreshToken;

    // Replaying the same (now-consumed) code is treated as theft.
    var replay = await _service.ExchangeTokenAsync(AuthCodeInput(code, verifier), default);
    replay.Success.Should().BeFalse();
    replay.Error.Should().Be("invalid_grant");

    // The refresh token minted from that code is revoked as part of the grant.
    (await _context.OAuthRefreshTokens.CountAsync(t => t.RevokedAt == null)).Should().Be(0);
    var refreshAttempt = await _service.ExchangeTokenAsync(RefreshInput(issuedRefresh), default);
    refreshAttempt.Success.Should().BeFalse();
    refreshAttempt.Error.Should().Be("invalid_grant");
  }

  // ------------------------------------------------------------------ refresh rotation + reuse

  [Fact]
  public async Task ExchangeToken_RefreshRotation_IssuesNewTokenAndRevokesPresented()
  {
    SeedClient();
    var refresh = await IssueRefreshTokenViaFullFlow(userId: 42);

    var rotated = await _service.ExchangeTokenAsync(RefreshInput(refresh), default);

    rotated.Success.Should().BeTrue();
    rotated.Token!.RefreshToken.Should().NotBe(refresh);
    // The presented token is now revoked; only the replacement is active.
    (await _context.OAuthRefreshTokens.CountAsync(t => t.RevokedAt == null)).Should().Be(1);
  }

  [Fact]
  public async Task ExchangeToken_ReusedRotatedRefreshToken_RevokesWholeFamily()
  {
    SeedClient();
    var r1 = await IssueRefreshTokenViaFullFlow(userId: 42);

    var rotated = await _service.ExchangeTokenAsync(RefreshInput(r1), default);
    var r2 = rotated.Token!.RefreshToken;

    // Replaying the already-rotated r1 is theft → revoke the entire family, including r2.
    var replay = await _service.ExchangeTokenAsync(RefreshInput(r1), default);
    replay.Success.Should().BeFalse();
    replay.Error.Should().Be("invalid_grant");

    (await _context.OAuthRefreshTokens.CountAsync(t => t.RevokedAt == null)).Should().Be(0);
    var r2Attempt = await _service.ExchangeTokenAsync(RefreshInput(r2), default);
    r2Attempt.Success.Should().BeFalse();
    r2Attempt.Error.Should().Be("invalid_grant");
  }

  // ------------------------------------------------------------------ deny + resource + expiry

  [Fact]
  public async Task Decide_Deny_ReturnsAccessDeniedRedirect()
  {
    SeedClient();
    var (_, challenge) = Pkce();
    var authorize = await _service.HandleAuthorizeAsync(ValidAuthorizeInput(challenge, state: "s1"), default);
    var requestId = RequestIdFromConsentUrl(authorize.RedirectUrl!);

    var decision = await _service.DecideAsync(requestId, userId: 42, approve: false, default);

    decision.Should().NotBeNull();
    var query = Query(decision!.RedirectUrl);
    query["error"].Should().Be("access_denied");
    query["state"].Should().Be("s1");
    query.ContainsKey("code").Should().BeFalse();
  }

  [Fact]
  public async Task HandleAuthorize_ResourceMismatch_RedirectsWithInvalidTarget()
  {
    SeedClient();
    var (_, challenge) = Pkce();
    var input = ValidAuthorizeInput(challenge, state: "s1");
    input.Resource = "https://attacker.example/mcp"; // not our ResourceUri

    var result = await _service.HandleAuthorizeAsync(input, default);

    result.Kind.Should().Be(AuthorizeOutcomeKind.RedirectWithError);
    Query(result.RedirectUrl!)["error"].Should().Be("invalid_target");
  }

  [Fact]
  public async Task HandleAuthorize_PlainPkceMethod_RedirectsWithInvalidRequest()
  {
    SeedClient();
    var (_, challenge) = Pkce();
    var input = ValidAuthorizeInput(challenge, state: "s1");
    input.CodeChallengeMethod = "plain"; // S256 is required

    var result = await _service.HandleAuthorizeAsync(input, default);

    result.Kind.Should().Be(AuthorizeOutcomeKind.RedirectWithError);
    Query(result.RedirectUrl!)["error"].Should().Be("invalid_request");
  }

  [Fact]
  public async Task HandleAuthorize_UnknownRedirectUri_ReturnsErrorPageNeverRedirects()
  {
    SeedClient();
    var (_, challenge) = Pkce();
    var input = ValidAuthorizeInput(challenge, state: "s1");
    input.RedirectUri = "https://client.example/evil"; // not registered

    var result = await _service.HandleAuthorizeAsync(input, default);

    result.Kind.Should().Be(AuthorizeOutcomeKind.ErrorPage);
    result.ErrorStatusCode.Should().Be(400);
    result.RedirectUrl.Should().BeNull();
  }

  [Fact]
  public async Task GetConsentView_ExpiredRequest_ReturnsNull()
  {
    SeedClient();
    var expired = new OAuthAuthorizationRequest
    {
      Id = Guid.NewGuid(),
      ClientId = ClientId,
      RedirectUri = RedirectUri,
      Scope = "buoyai:read",
      CodeChallenge = "challenge",
      CodeChallengeMethod = "S256",
      CreatedAt = DateTime.UtcNow.AddMinutes(-20),
      ExpiresAt = DateTime.UtcNow.AddMinutes(-10),
    };
    _context.OAuthAuthorizationRequests.Add(expired);
    await _context.SaveChangesAsync();

    (await _service.GetConsentViewAsync(expired.Id, default)).Should().BeNull();
  }

  // ------------------------------------------------------------------ CIMD

  [Fact]
  public async Task HandleAuthorize_ValidCimdDocument_ResolvesClientAndRedirectsToConsent()
  {
    const string metadataUrl = "https://client.example/metadata";
    _cimdResponder = _ => JsonResponse($@"{{ ""client_name"": ""CIMD App"", ""redirect_uris"": [""{RedirectUri}""] }}");
    var (_, challenge) = Pkce();

    var input = ValidAuthorizeInput(challenge, state: "s1");
    input.ClientId = metadataUrl;

    var result = await _service.HandleAuthorizeAsync(input, default);

    result.Kind.Should().Be(AuthorizeOutcomeKind.RedirectToConsent);
    var cached = await _context.OAuthClients.FindAsync(metadataUrl);
    cached.Should().NotBeNull();
    cached!.IsCimd.Should().BeTrue();
  }

  [Fact]
  public async Task HandleAuthorize_CimdWrongContentType_ReturnsErrorPage()
  {
    _cimdResponder = _ =>
    {
      var resp = new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent($@"{{ ""redirect_uris"": [""{RedirectUri}""] }}", Encoding.UTF8, "text/plain"),
      };
      return resp;
    };
    var (_, challenge) = Pkce();
    var input = ValidAuthorizeInput(challenge, state: "s1");
    input.ClientId = "https://client.example/metadata";

    var result = await _service.HandleAuthorizeAsync(input, default);

    result.Kind.Should().Be(AuthorizeOutcomeKind.ErrorPage);
  }

  [Fact]
  public async Task HandleAuthorize_CimdOversizedDocument_ReturnsErrorPage()
  {
    var padding = new string('x', 70 * 1024); // > 64KB cap
    _cimdResponder = _ => JsonResponse($@"{{ ""pad"": ""{padding}"", ""redirect_uris"": [""{RedirectUri}""] }}");
    var (_, challenge) = Pkce();
    var input = ValidAuthorizeInput(challenge, state: "s1");
    input.ClientId = "https://client.example/metadata";

    var result = await _service.HandleAuthorizeAsync(input, default);

    result.Kind.Should().Be(AuthorizeOutcomeKind.ErrorPage);
  }

  [Fact]
  public async Task HandleAuthorize_CimdRedirectUriMismatch_ReturnsErrorPage()
  {
    // Document is valid but does not list the requested redirect_uri.
    _cimdResponder = _ => JsonResponse(@"{ ""redirect_uris"": [""https://client.example/other""] }");
    var (_, challenge) = Pkce();
    var input = ValidAuthorizeInput(challenge, state: "s1");
    input.ClientId = "https://client.example/metadata";

    var result = await _service.HandleAuthorizeAsync(input, default);

    result.Kind.Should().Be(AuthorizeOutcomeKind.ErrorPage);
  }

  [Fact]
  public async Task HandleAuthorize_NonHttpsCimdClientId_ReturnsErrorPage()
  {
    var (_, challenge) = Pkce();
    var input = ValidAuthorizeInput(challenge, state: "s1");
    input.ClientId = "http://client.example/metadata"; // http, not https

    var result = await _service.HandleAuthorizeAsync(input, default);

    result.Kind.Should().Be(AuthorizeOutcomeKind.ErrorPage);
  }

  // ------------------------------------------------------------------ SSRF guard (IP classifier)

  [Theory]
  [InlineData("127.0.0.1")]     // loopback
  [InlineData("10.0.0.5")]      // RFC1918 /8
  [InlineData("172.16.5.5")]    // RFC1918 /12
  [InlineData("172.31.255.1")]  // RFC1918 /12 upper bound
  [InlineData("192.168.1.1")]   // RFC1918 /16
  [InlineData("169.254.169.254")] // link-local / cloud metadata
  [InlineData("100.64.0.1")]    // CGNAT /10
  [InlineData("0.0.0.0")]       // unspecified / this-network
  [InlineData("224.0.0.1")]     // multicast
  [InlineData("::1")]           // IPv6 loopback
  [InlineData("fe80::1")]       // IPv6 link-local
  [InlineData("fc00::1")]       // IPv6 unique-local (ULA)
  [InlineData("fd12:3456::1")]  // IPv6 ULA
  [InlineData("ff02::1")]       // IPv6 multicast
  [InlineData("::")]            // IPv6 unspecified
  [InlineData("::ffff:10.0.0.1")] // IPv4-mapped private
  public void IsPublicIpAddress_NonPublicAddress_ReturnsFalse(string ip)
  {
    OAuthConnectService.IsPublicIpAddress(IPAddress.Parse(ip)).Should().BeFalse();
  }

  [Theory]
  [InlineData("93.184.216.34")] // public IPv4
  [InlineData("8.8.8.8")]       // public IPv4
  [InlineData("172.15.0.1")]    // just below RFC1918 /12
  [InlineData("172.32.0.1")]    // just above RFC1918 /12
  [InlineData("100.63.0.1")]    // just below CGNAT /10
  [InlineData("100.128.0.1")]   // just above CGNAT /10
  [InlineData("2606:4700:4700::1111")] // public IPv6
  public void IsPublicIpAddress_PublicAddress_ReturnsTrue(string ip)
  {
    OAuthConnectService.IsPublicIpAddress(IPAddress.Parse(ip)).Should().BeTrue();
  }

  [Fact]
  public async Task HandleAuthorize_CimdClientIdIsPrivateIpLiteral_ReturnsErrorPageAndDoesNotFetch()
  {
    var fetched = false;
    _cimdResponder = _ => { fetched = true; return JsonResponse(@"{ ""redirect_uris"": [""" + RedirectUri + @"""] }"); };
    var (_, challenge) = Pkce();
    var input = ValidAuthorizeInput(challenge, state: "s1");
    input.ClientId = "https://169.254.169.254/metadata"; // cloud metadata endpoint as an IP literal

    var result = await _service.HandleAuthorizeAsync(input, default);

    result.Kind.Should().Be(AuthorizeOutcomeKind.ErrorPage);
    fetched.Should().BeFalse(); // blocked before any socket/fetch — IP literal classified directly
  }

  [Fact]
  public async Task HandleAuthorize_CimdHostResolvesToPrivateIp_ReturnsErrorPageAndDoesNotFetch()
  {
    var fetched = false;
    _cimdResponder = _ => { fetched = true; return JsonResponse(@"{ ""redirect_uris"": [""" + RedirectUri + @"""] }"); };
    _hostResolver = (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3") }); // internal answer
    var (_, challenge) = Pkce();
    var input = ValidAuthorizeInput(challenge, state: "s1");
    input.ClientId = "https://internal.attacker.example/metadata";

    var result = await _service.HandleAuthorizeAsync(input, default);

    result.Kind.Should().Be(AuthorizeOutcomeKind.ErrorPage);
    fetched.Should().BeFalse();
  }

  [Fact]
  public async Task HandleAuthorize_CimdHostDnsResolutionFails_ReturnsErrorPage()
  {
    _cimdResponder = _ => JsonResponse(@"{ ""redirect_uris"": [""" + RedirectUri + @"""] }");
    _hostResolver = (_, _) => throw new System.Net.Sockets.SocketException(); // unresolvable host
    var (_, challenge) = Pkce();
    var input = ValidAuthorizeInput(challenge, state: "s1");
    input.ClientId = "https://does-not-resolve.example/metadata";

    var result = await _service.HandleAuthorizeAsync(input, default);

    result.Kind.Should().Be(AuthorizeOutcomeKind.ErrorPage);
  }

  // ------------------------------------------------------------------ CIMD redirect_uri scheme filter (FIX 3)

  [Fact]
  public async Task HandleAuthorize_CimdRedirectUriWithDangerousScheme_FiltersItOut()
  {
    // Document lists a javascript: URI alongside the legitimate https callback. The dangerous
    // scheme must be dropped, and the client still resolves on the valid https redirect_uri.
    _cimdResponder = _ => JsonResponse(
      @"{ ""redirect_uris"": [""javascript:alert(1)"", """ + RedirectUri + @"""] }");
    var (_, challenge) = Pkce();
    var input = ValidAuthorizeInput(challenge, state: "s1");
    input.ClientId = "https://client.example/metadata";

    var result = await _service.HandleAuthorizeAsync(input, default);

    result.Kind.Should().Be(AuthorizeOutcomeKind.RedirectToConsent);
    var cached = await _context.OAuthClients.FindAsync("https://client.example/metadata");
    cached.Should().NotBeNull();
    cached!.RedirectUris.Should().Contain(RedirectUri);
    cached.RedirectUris.Should().NotContain("javascript:");
  }

  // ------------------------------------------------------------------ refresh-token client binding (FIX 2)

  [Fact]
  public async Task ExchangeToken_RefreshWithMismatchedClientId_ReturnsInvalidGrantAndDoesNotRotate()
  {
    SeedClient();
    var refresh = await IssueRefreshTokenViaFullFlow(userId: 42);

    // Client B presents client A's refresh token.
    var attempt = await _service.ExchangeTokenAsync(new OAuthTokenInput
    {
      GrantType = "refresh_token",
      RefreshToken = refresh,
      ClientId = "some-other-client",
    }, default);

    attempt.Success.Should().BeFalse();
    attempt.Error.Should().Be("invalid_grant");

    // The real client's token must NOT be rotated or revoked by a mismatched attempt.
    (await _context.OAuthRefreshTokens.CountAsync(t => t.RevokedAt == null)).Should().Be(1);
    var legit = await _service.ExchangeTokenAsync(RefreshInput(refresh), default);
    legit.Success.Should().BeTrue(); // still usable by its rightful client
  }

  // ------------------------------------------------------------------ configured-client upsert

  [Fact]
  public async Task UpsertConfiguredClients_AddsThenUpdatesOnChangedRedirectUris()
  {
    _options.Clients.Add(new McpClientConfig
    {
      ClientId = "buoyai-e2e-test",
      ClientName = "E2E Test Client",
      RedirectUris = new List<string> { "http://localhost:9999/callback" },
    });

    await _service.UpsertConfiguredClientsAsync(default);

    // AsNoTracking: the service mutates rows on its own factory context, so the assertion context
    // must read fresh from the store rather than return a cached tracked copy.
    var added = await _context.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == "buoyai-e2e-test");
    added.Should().NotBeNull();
    added!.IsCimd.Should().BeFalse();
    added.RedirectUris.Should().Contain("http://localhost:9999/callback");

    // Change the configured redirect URIs and re-run — same row, updated value (idempotent upsert).
    _options.Clients[0].RedirectUris = new List<string> { "http://localhost:8888/callback" };
    await _service.UpsertConfiguredClientsAsync(default);

    (await _context.OAuthClients.CountAsync(c => c.ClientId == "buoyai-e2e-test")).Should().Be(1);
    var updated = await _context.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == "buoyai-e2e-test");
    updated!.RedirectUris.Should().Contain("http://localhost:8888/callback");
    updated.RedirectUris.Should().NotContain("9999");
  }

  // ------------------------------------------------------------------ helpers

  private void SeedClient()
  {
    _context.OAuthClients.Add(new OAuthClient
    {
      ClientId = ClientId,
      ClientName = "Test Client",
      RedirectUris = $"[\"{RedirectUri}\"]",
      IsCimd = false,
      CreatedAt = DateTime.UtcNow,
    });
    _context.SaveChanges();
  }

  private OAuthAuthorizeInput ValidAuthorizeInput(string challenge, string? state) => new()
  {
    ResponseType = "code",
    ClientId = ClientId,
    RedirectUri = RedirectUri,
    Scope = "buoyai:read",
    State = state,
    CodeChallenge = challenge,
    CodeChallengeMethod = "S256",
  };

  private static OAuthTokenInput AuthCodeInput(string code, string verifier) => new()
  {
    GrantType = "authorization_code",
    Code = code,
    RedirectUri = RedirectUri,
    ClientId = ClientId,
    CodeVerifier = verifier,
  };

  private static OAuthTokenInput RefreshInput(string refreshToken) => new()
  {
    GrantType = "refresh_token",
    RefreshToken = refreshToken,
  };

  private async Task<Guid> ApproveAndGetRequestId(string challenge, int userId)
  {
    var authorize = await _service.HandleAuthorizeAsync(ValidAuthorizeInput(challenge, state: "s1"), default);
    return RequestIdFromConsentUrl(authorize.RedirectUrl!);
  }

  private async Task<string> IssueRefreshTokenViaFullFlow(int userId)
  {
    var (verifier, challenge) = Pkce();
    var requestId = await ApproveAndGetRequestId(challenge, userId);
    var code = Query((await _service.DecideAsync(requestId, userId, true, default))!.RedirectUrl)["code"];
    var token = await _service.ExchangeTokenAsync(AuthCodeInput(code, verifier), default);
    token.Success.Should().BeTrue();
    return token.Token!.RefreshToken;
  }

  private static (string verifier, string challenge) Pkce()
  {
    var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
    var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    return (verifier, challenge);
  }

  private static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

  private static Guid RequestIdFromConsentUrl(string url) => Guid.Parse(Query(url)["request"]);

  private static Dictionary<string, string> Query(string url)
  {
    var uri = new Uri(url, UriKind.Absolute);
    return uri.Query.TrimStart('?')
      .Split('&', StringSplitOptions.RemoveEmptyEntries)
      .Select(p => p.Split('=', 2))
      .ToDictionary(
        p => Uri.UnescapeDataString(p[0]),
        p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty);
  }

  private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
  {
    Content = new StringContent(json, Encoding.UTF8, "application/json"),
  };

  /// <summary>Hands out fresh contexts over the same in-memory store the test asserts against.</summary>
  private sealed class SharedOptionsDbContextFactory : IDbContextFactory<BuoyAIDbContext>
  {
    private readonly DbContextOptions<BuoyAIDbContext> _options;
    public SharedOptionsDbContextFactory(DbContextOptions<BuoyAIDbContext> options) => _options = options;
    public BuoyAIDbContext CreateDbContext() => new(_options);
  }

  private sealed class StubHttpMessageHandler : HttpMessageHandler
  {
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      => Task.FromResult(_responder(request));
  }
}
