using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs.OAuth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// BuoyAI Connect OAuth 2.1 authorization server (RFC #782). Owns client resolution (pre-registered
/// + Client ID Metadata Document), the authorize-request lifecycle, PKCE S256 verification,
/// single-use authorization codes, access-token minting (HS256 with Mcp:SigningKey, falling back to Jwt:Key, with an
/// MCP-pinned iss/aud so a Connect token is NEVER valid for the first-party API), and rotating
/// refresh tokens with reuse detection.
///
/// Each operation runs on its own short-lived <see cref="BuoyAIDbContext"/> from the factory, like
/// <see cref="RefreshTokenService"/> — the controller has no other DB work in flight, but this keeps
/// the service self-contained and matches the existing token-issuance pattern.
///
/// SECURITY: exact redirect_uri string match; PKCE required (S256 only); 60s single-use codes;
/// 10-min authorize requests; opaque refresh tokens stored SHA-256 hashed; CIMD fetch is https-only,
/// no redirects, 64KB-capped, JSON content-type enforced; tokens and PKCE verifiers are never logged.
/// </summary>
public class OAuthConnectService : IOAuthConnectService
{
  private static readonly TimeSpan AuthorizationRequestTtl = TimeSpan.FromMinutes(10);
  private static readonly TimeSpan AuthorizationCodeTtl = TimeSpan.FromSeconds(60);
  private static readonly TimeSpan RefreshTokenTtl = TimeSpan.FromDays(30);
  private static readonly TimeSpan CimdCacheTtl = TimeSpan.FromHours(24);
  private const int AccessTokenSeconds = 3600;
  private const long CimdMaxBytes = 64 * 1024;
  private const string SupportedScope = "buoyai:read";
  private const string ScopeDescription =
    "Read your budgets, transactions, accounts, savings goals and insights";

  // The narrow, non-financial write scope: lets a connected assistant store durable notes
  // (goals, preferences, dates) in your BuoyAI memory. It CANNOT move, spend, or transfer money.
  private const string MemoryWriteScope = "buoyai:write:memory";
  private const string MemoryWriteScopeDescription =
    "Remember notes and preferences you share (never moves or spends money)";

  // Both scopes are granted together — BuoyAI Connect is one product (see and remember), so the
  // consent screen shows both and the token carries both. Ordered read-first for display.
  private static readonly string GrantedScope = $"{SupportedScope} {MemoryWriteScope}";
  private static readonly string[] GrantedScopes = { SupportedScope, MemoryWriteScope };
  private static readonly string[] GrantedScopeDescriptions = { ScopeDescription, MemoryWriteScopeDescription };

  private readonly IDbContextFactory<Data.BuoyAIDbContext> _contextFactory;
  private readonly IHttpClientFactory _httpClientFactory;
  private readonly IEntitlementService _entitlements;
  private readonly McpOptions _options;
  private readonly string _jwtKey;
  private readonly ILogger<OAuthConnectService> _logger;
  // DNS resolution seam for the CIMD SSRF guard. Defaults to the real resolver; tests inject a
  // fake so they can exercise both the "resolves to a public IP" and "resolves to a private IP"
  // branches without depending on live DNS.
  private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHostAddresses;

  public OAuthConnectService(
    IDbContextFactory<Data.BuoyAIDbContext> contextFactory,
    IHttpClientFactory httpClientFactory,
    IEntitlementService entitlements,
    IOptions<McpOptions> options,
    IConfiguration configuration,
    ILogger<OAuthConnectService> logger,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolveHostAddresses = null)
  {
    _contextFactory = contextFactory;
    _httpClientFactory = httpClientFactory;
    _entitlements = entitlements;
    _options = options.Value;
    _jwtKey = BuoyAI.Infrastructure.Configuration.StartupSecretsValidator.ResolveMcpSigningKey(configuration);
    _logger = logger;
    _resolveHostAddresses = resolveHostAddresses ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
  }

  private string Issuer => _options.Issuer ?? throw new InvalidOperationException("Mcp:Issuer not configured");
  private string ResourceUri => _options.ResourceUri ?? throw new InvalidOperationException("Mcp:ResourceUri not configured");

  // ---------------------------------------------------------------------------------------------
  // Authorize
  // ---------------------------------------------------------------------------------------------

  public async Task<OAuthAuthorizeResult> HandleAuthorizeAsync(OAuthAuthorizeInput input, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(input.ClientId))
      return OAuthAuthorizeResult.Error(400, "Invalid request", "Missing client_id.");
    if (string.IsNullOrWhiteSpace(input.RedirectUri))
      return OAuthAuthorizeResult.Error(400, "Invalid request", "Missing redirect_uri.");

    await using var ctx = await _contextFactory.CreateDbContextAsync(ct);

    var client = await ResolveClientAsync(ctx, input.ClientId!, ct);
    if (client == null)
      return OAuthAuthorizeResult.Error(400, "Unknown client", "This application could not be verified.");

    // Exact redirect_uri string match — the anchor of the whole flow. A mismatch (or unknown
    // client) NEVER redirects, because an open redirect here would be an exfiltration primitive.
    var registered = ParseRedirectUris(client.RedirectUris);
    if (!registered.Contains(input.RedirectUri!, StringComparer.Ordinal))
      return OAuthAuthorizeResult.Error(400, "Invalid redirect URI", "The redirect_uri does not match a registered value.");

    // redirect_uri is trusted from here on — recoverable errors bounce back to the client per RFC 6749.
    if (!string.Equals(input.ResponseType, "code", StringComparison.Ordinal))
      return OAuthAuthorizeResult.RedirectError(BuildErrorRedirect(input.RedirectUri!, "unsupported_response_type", input.State));

    if (string.IsNullOrWhiteSpace(input.CodeChallenge))
      return OAuthAuthorizeResult.RedirectError(BuildErrorRedirect(input.RedirectUri!, "invalid_request", input.State));

    // PKCE is mandatory and S256-only — "plain" is explicitly rejected.
    if (!string.Equals(input.CodeChallengeMethod, "S256", StringComparison.Ordinal))
      return OAuthAuthorizeResult.RedirectError(BuildErrorRedirect(input.RedirectUri!, "invalid_request", input.State));

    // RFC 8707 resource indicator: optional, but if present it must be exactly our resource.
    if (!string.IsNullOrWhiteSpace(input.Resource) && !string.Equals(input.Resource, ResourceUri, StringComparison.Ordinal))
      return OAuthAuthorizeResult.RedirectError(BuildErrorRedirect(input.RedirectUri!, "invalid_target", input.State));

    var request = new OAuthAuthorizationRequest
    {
      Id = Guid.NewGuid(),
      ClientId = client.ClientId,
      RedirectUri = input.RedirectUri!,
      Scope = GrantedScope,
      State = input.State,
      CodeChallenge = input.CodeChallenge!,
      CodeChallengeMethod = "S256",
      Resource = string.IsNullOrWhiteSpace(input.Resource) ? null : input.Resource,
      Consumed = false,
      Approved = null,
      CreatedAt = DateTime.UtcNow,
      ExpiresAt = DateTime.UtcNow.Add(AuthorizationRequestTtl),
    };
    ctx.OAuthAuthorizationRequests.Add(request);
    await ctx.SaveChangesAsync(ct);

    var consentUrl = $"{Issuer.TrimEnd('/')}/app/connect?request={request.Id}";
    return OAuthAuthorizeResult.Consent(consentUrl);
  }

  // ---------------------------------------------------------------------------------------------
  // Consent view + decision
  // ---------------------------------------------------------------------------------------------

  public async Task<OAuthConsentView?> GetConsentViewAsync(Guid requestId, CancellationToken ct)
  {
    await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
    var request = await ctx.OAuthAuthorizationRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
    if (request == null || request.Consumed || request.Approved != null || request.ExpiresAt <= DateTime.UtcNow)
      return null;

    var client = await ctx.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == request.ClientId, ct);
    return new OAuthConsentView
    {
      ClientName = client?.ClientName ?? request.ClientId,
      ClientUri = client?.ClientUri,
      LogoUri = client?.LogoUri,
      Scopes = GrantedScopes,
      ScopeDescriptions = GrantedScopeDescriptions,
      ExpiresAt = request.ExpiresAt,
    };
  }

  public async Task<OAuthDecisionResult?> DecideAsync(Guid requestId, int userId, bool approve, CancellationToken ct)
  {
    await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
    var request = await ctx.OAuthAuthorizationRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
    if (request == null || request.Consumed || request.Approved != null || request.ExpiresAt <= DateTime.UtcNow)
      return null;

    // Bind the request to the acting user at decision time (never trusted from the client).
    request.UserId = userId;

    if (!approve)
    {
      request.Approved = false;
      await ctx.SaveChangesAsync(ct);
      return new OAuthDecisionResult { RedirectUrl = BuildErrorRedirect(request.RedirectUri, "access_denied", request.State) };
    }

    request.Approved = true;
    request.Code = GenerateOpaqueToken();
    request.CodeExpiresAt = DateTime.UtcNow.Add(AuthorizationCodeTtl);
    await ctx.SaveChangesAsync(ct);

    return new OAuthDecisionResult { RedirectUrl = BuildSuccessRedirect(request.RedirectUri, request.Code!, request.State) };
  }

  // ---------------------------------------------------------------------------------------------
  // Token endpoint
  // ---------------------------------------------------------------------------------------------

  public async Task<OAuthTokenResult> ExchangeTokenAsync(OAuthTokenInput input, CancellationToken ct)
  {
    return input.GrantType switch
    {
      "authorization_code" => await ExchangeAuthorizationCodeAsync(input, ct),
      "refresh_token" => await ExchangeRefreshTokenAsync(input, ct),
      null or "" => OAuthTokenResult.Fail(400, "invalid_request", "grant_type is required."),
      _ => OAuthTokenResult.Fail(400, "unsupported_grant_type", $"grant_type '{input.GrantType}' is not supported."),
    };
  }

  private async Task<OAuthTokenResult> ExchangeAuthorizationCodeAsync(OAuthTokenInput input, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(input.Code))
      return OAuthTokenResult.Fail(400, "invalid_request", "code is required.");
    if (string.IsNullOrWhiteSpace(input.ClientId))
      return OAuthTokenResult.Fail(400, "invalid_request", "client_id is required.");
    if (string.IsNullOrWhiteSpace(input.RedirectUri))
      return OAuthTokenResult.Fail(400, "invalid_request", "redirect_uri is required.");
    if (string.IsNullOrWhiteSpace(input.CodeVerifier))
      return OAuthTokenResult.Fail(400, "invalid_request", "code_verifier is required.");

    await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
    var request = await ctx.OAuthAuthorizationRequests.FirstOrDefaultAsync(r => r.Code == input.Code, ct);
    if (request == null)
      return OAuthTokenResult.Fail(400, "invalid_grant", "Invalid or expired authorization code.");

    // Code reuse: a consumed code being replayed is a theft signal. Revoke every refresh token for
    // this (user, client) grant and reject. (The one-time access token is short-lived and stateless.)
    if (request.Consumed)
    {
      if (request.UserId.HasValue)
        await RevokeGrantRefreshTokensAsync(ctx, request.UserId.Value, request.ClientId, ct);
      _logger.LogWarning(
        "OAuth authorization-code reuse detected for client {ClientId}, user {UserId}; revoked grant tokens.",
        request.ClientId, request.UserId);
      return OAuthTokenResult.Fail(400, "invalid_grant", "Authorization code already used.");
    }

    if (request.Approved != true || !request.UserId.HasValue)
      return OAuthTokenResult.Fail(400, "invalid_grant", "Authorization code is not usable.");
    if (request.CodeExpiresAt == null || request.CodeExpiresAt <= DateTime.UtcNow)
      return OAuthTokenResult.Fail(400, "invalid_grant", "Authorization code expired.");
    if (!string.Equals(request.ClientId, input.ClientId, StringComparison.Ordinal))
      return OAuthTokenResult.Fail(400, "invalid_grant", "client_id does not match the authorization request.");
    if (!string.Equals(request.RedirectUri, input.RedirectUri, StringComparison.Ordinal))
      return OAuthTokenResult.Fail(400, "invalid_grant", "redirect_uri does not match the authorization request.");
    if (!VerifyPkce(input.CodeVerifier!, request.CodeChallenge))
      return OAuthTokenResult.Fail(400, "invalid_grant", "PKCE verification failed.");

    // If a resource was pinned at authorize time and one is supplied now, they must agree.
    if (!string.IsNullOrWhiteSpace(input.Resource) && request.Resource != null
        && !string.Equals(input.Resource, request.Resource, StringComparison.Ordinal))
      return OAuthTokenResult.Fail(400, "invalid_target", "resource does not match the authorization request.");

    request.Consumed = true;
    await ctx.SaveChangesAsync(ct);

    var resource = request.Resource ?? ResourceUri;
    var refreshToken = await IssueRefreshTokenAsync(ctx, request.UserId.Value, request.ClientId, request.Scope, resource, ct);
    var accessToken = await MintAccessTokenAsync(request.UserId.Value, request.ClientId, request.Scope, resource);

    return OAuthTokenResult.Ok(new OAuthTokenResponse
    {
      AccessToken = accessToken,
      RefreshToken = refreshToken,
      Scope = request.Scope,
      ExpiresIn = AccessTokenSeconds,
    });
  }

  private async Task<OAuthTokenResult> ExchangeRefreshTokenAsync(OAuthTokenInput input, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(input.RefreshToken))
      return OAuthTokenResult.Fail(400, "invalid_request", "refresh_token is required.");

    await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
    // RFC 6749 §6: bind the refresh grant to its client. When a client_id is presented it must match
    // the one the token was issued to; a mismatch is rejected WITHOUT rotating/revoking the token.
    var rotated = await RotateRefreshTokenAsync(ctx, input.RefreshToken!, input.ClientId, ct);
    if (rotated == null)
      return OAuthTokenResult.Fail(400, "invalid_grant", "Invalid or expired refresh token.");

    if (!string.IsNullOrWhiteSpace(input.Resource) && rotated.Resource != null
        && !string.Equals(input.Resource, rotated.Resource, StringComparison.Ordinal))
      return OAuthTokenResult.Fail(400, "invalid_target", "resource does not match the grant.");

    var resource = rotated.Resource ?? ResourceUri;
    var accessToken = await MintAccessTokenAsync(rotated.UserId, rotated.ClientId, rotated.Scope, resource);

    return OAuthTokenResult.Ok(new OAuthTokenResponse
    {
      AccessToken = accessToken,
      RefreshToken = rotated.RawToken,
      Scope = rotated.Scope,
      ExpiresIn = AccessTokenSeconds,
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Client resolution (pre-registered + CIMD)
  // ---------------------------------------------------------------------------------------------

  private async Task<OAuthClient?> ResolveClientAsync(Data.BuoyAIDbContext ctx, string clientId, CancellationToken ct)
  {
    var existing = await ctx.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == clientId, ct);

    // Pre-registered clients always win and are never treated as CIMD.
    if (existing != null && !existing.IsCimd)
      return existing;

    // Cached CIMD that is still fresh (<24h) — reuse without a refetch.
    if (existing != null && existing.IsCimd
        && existing.CimdFetchedAt != null
        && existing.CimdFetchedAt.Value.Add(CimdCacheTtl) > DateTime.UtcNow)
      return existing;

    // CIMD path: the client_id must itself be a fetchable https metadata-document URL.
    if (!IsValidCimdClientId(clientId))
      return existing; // no valid CIMD and not pre-registered → unresolved

    var metadata = await FetchCimdMetadataAsync(clientId, ct);
    if (metadata == null || metadata.RedirectUris.Count == 0)
      return existing; // fetch/validation failed → fall back to stale cache if any, else null

    var redirectJson = JsonSerializer.Serialize(metadata.RedirectUris);
    if (existing == null)
    {
      existing = new OAuthClient
      {
        ClientId = clientId,
        ClientName = metadata.ClientName ?? clientId,
        ClientUri = metadata.ClientUri,
        LogoUri = metadata.LogoUri,
        RedirectUris = redirectJson,
        IsCimd = true,
        CimdFetchedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
      };
      ctx.OAuthClients.Add(existing);
    }
    else
    {
      existing.ClientName = metadata.ClientName ?? clientId;
      existing.ClientUri = metadata.ClientUri;
      existing.LogoUri = metadata.LogoUri;
      existing.RedirectUris = redirectJson;
      existing.IsCimd = true;
      existing.CimdFetchedAt = DateTime.UtcNow;
    }
    await ctx.SaveChangesAsync(ct);
    return existing;
  }

  private static bool IsValidCimdClientId(string clientId)
  {
    if (!Uri.TryCreate(clientId, UriKind.Absolute, out var uri))
      return false;
    if (uri.Scheme != Uri.UriSchemeHttps)
      return false;
    // A fragment on the client_id URL is disallowed (RFC: metadata document URLs carry no fragment).
    if (!string.IsNullOrEmpty(uri.Fragment))
      return false;
    return true;
  }

  private async Task<CimdMetadata?> FetchCimdMetadataAsync(string url, CancellationToken ct)
  {
    try
    {
      // SSRF guard: this endpoint is anonymous and the client_id is an attacker-controlled URL, so
      // before we ever open a socket we resolve the host and reject if ANY resolved address is
      // non-public (loopback, RFC1918, link-local, CGNAT, ULA, multicast, unspecified). This blocks
      // pointing client_id at 169.254.169.254 (cloud metadata), 10.x, localhost, etc. The named
      // client's ConnectCallback (DI) re-validates the IP it actually dials to close DNS-rebind.
      if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || !await IsPublicHostAsync(target, ct))
      {
        _logger.LogWarning("CIMD fetch blocked: metadata document host does not resolve to a public address.");
        return null;
      }

      // The named client is configured (in DI) with auto-redirect DISABLED, so a 3xx to any host —
      // https or not — is simply not followed and surfaces as a non-success status here.
      var client = _httpClientFactory.CreateClient("oauth-cimd");
      client.Timeout = TimeSpan.FromSeconds(10);

      using var req = new HttpRequestMessage(HttpMethod.Get, url);
      req.Headers.Accept.ParseAdd("application/json");
      using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
      if (!resp.IsSuccessStatusCode)
        return null;

      var mediaType = resp.Content.Headers.ContentType?.MediaType;
      if (mediaType == null || !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        return null;

      // Cap the body at 64KB — read incrementally and bail the moment we exceed it.
      await using var stream = await resp.Content.ReadAsStreamAsync(ct);
      using var buffer = new MemoryStream();
      var chunk = new byte[8192];
      int read;
      while ((read = await stream.ReadAsync(chunk, ct)) > 0)
      {
        if (buffer.Length + read > CimdMaxBytes)
          return null;
        buffer.Write(chunk, 0, read);
      }

      buffer.Position = 0;
      using var doc = await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
      var root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
        return null;

      var redirectUris = new List<string>();
      if (root.TryGetProperty("redirect_uris", out var uris) && uris.ValueKind == JsonValueKind.Array)
      {
        foreach (var u in uris.EnumerateArray())
        {
          if (u.ValueKind == JsonValueKind.String)
          {
            var value = u.GetString();
            // Defense-in-depth: only accept http/https redirect targets. Keep http so a CIMD client
            // can register a localhost dev callback, but drop javascript:/data:/file:/etc. so a
            // hostile metadata doc can't smuggle a dangerous-scheme URI into the registered set.
            if (!string.IsNullOrWhiteSpace(value)
                && Uri.TryCreate(value, UriKind.Absolute, out var ru)
                && (ru.Scheme == Uri.UriSchemeHttp || ru.Scheme == Uri.UriSchemeHttps))
              redirectUris.Add(value!);
          }
        }
      }
      if (redirectUris.Count == 0)
        return null;

      return new CimdMetadata
      {
        ClientName = GetStringOrNull(root, "client_name"),
        ClientUri = GetStringOrNull(root, "client_uri"),
        LogoUri = GetStringOrNull(root, "logo_uri"),
        RedirectUris = redirectUris,
      };
    }
    catch (Exception ex)
    {
      // Never let a hostile/broken metadata document throw into the request — treat as unresolved.
      _logger.LogWarning(ex, "CIMD fetch failed for client metadata document.");
      return null;
    }
  }

  private static string? GetStringOrNull(JsonElement obj, string property) =>
    obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

  // ---------------------------------------------------------------------------------------------
  // SSRF guard for the CIMD fetch
  // ---------------------------------------------------------------------------------------------

  /// <summary>
  /// True only if every address the host resolves to is publicly routable. If the host is an IP
  /// literal it is classified directly (no DNS). A hostname is resolved via the injected resolver
  /// and rejected on any resolution failure or if it yields no addresses. Used before the CIMD
  /// fetch so an anonymous caller can't aim client_id at an internal target.
  /// </summary>
  private async Task<bool> IsPublicHostAsync(Uri uri, CancellationToken ct)
  {
    // IP-literal host: classify directly, never touch DNS (uri.DnsSafeHost strips [] from IPv6).
    if (IPAddress.TryParse(uri.DnsSafeHost, out var literal))
      return IsPublicIpAddress(literal);

    IPAddress[] addresses;
    try
    {
      addresses = await _resolveHostAddresses(uri.DnsSafeHost, ct);
    }
    catch (Exception ex)
    {
      // Reject on DNS failure — safer to refuse an unresolvable/broken host than to fetch it.
      _logger.LogWarning(ex, "CIMD host DNS resolution failed; treating as non-public.");
      return false;
    }

    if (addresses.Length == 0)
      return false;
    // Every resolved address must be public — a single private/link-local answer is a rejection.
    return addresses.All(IsPublicIpAddress);
  }

  /// <summary>
  /// Classifies an <see cref="IPAddress"/> as publicly routable. Rejects loopback, unspecified,
  /// multicast, IPv4 private (RFC1918), link-local (169.254/16, fe80::/10), CGNAT (100.64/10),
  /// IPv6 unique-local (fc00::/7), and IPv4-mapped forms of all of the above. Public by exclusion.
  /// Also referenced by the "oauth-cimd" HttpClient ConnectCallback (DI) to re-check the dialed IP.
  /// </summary>
  public static bool IsPublicIpAddress(IPAddress ip)
  {
    // Normalize IPv4-mapped IPv6 (::ffff:a.b.c.d) down to IPv4 so the v4 range checks apply.
    if (ip.IsIPv4MappedToIPv6)
      ip = ip.MapToIPv4();

    if (IPAddress.IsLoopback(ip))
      return false;

    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
    {
      if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
        return false;
      if (ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast)
        return false;
      return true;
    }

    // IPv4 range checks by octet.
    var b = ip.GetAddressBytes();
    if (b.Length != 4)
      return false; // unknown family → refuse

    // 0.0.0.0/8 (this-network / unspecified)
    if (b[0] == 0) return false;
    // 10.0.0.0/8
    if (b[0] == 10) return false;
    // 127.0.0.0/8 (loopback — already caught, kept for clarity)
    if (b[0] == 127) return false;
    // 169.254.0.0/16 (link-local)
    if (b[0] == 169 && b[1] == 254) return false;
    // 172.16.0.0/12
    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
    // 192.168.0.0/16
    if (b[0] == 192 && b[1] == 168) return false;
    // 100.64.0.0/10 (CGNAT)
    if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
    // 224.0.0.0/4 (multicast) + 240.0.0.0/4 (reserved/limited-broadcast)
    if (b[0] >= 224) return false;

    return true;
  }

  // ---------------------------------------------------------------------------------------------
  // Configured-client upsert (startup)
  // ---------------------------------------------------------------------------------------------

  public async Task UpsertConfiguredClientsAsync(CancellationToken ct)
  {
    if (_options.Clients.Count == 0)
      return;

    await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
    foreach (var configured in _options.Clients)
    {
      if (string.IsNullOrWhiteSpace(configured.ClientId) || configured.RedirectUris.Count == 0)
      {
        _logger.LogWarning("Skipping malformed Mcp:Clients entry (missing ClientId or RedirectUris).");
        continue;
      }

      var redirectJson = JsonSerializer.Serialize(configured.RedirectUris);
      var existing = await ctx.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == configured.ClientId, ct);
      if (existing == null)
      {
        ctx.OAuthClients.Add(new OAuthClient
        {
          ClientId = configured.ClientId,
          ClientName = configured.ClientName,
          ClientUri = configured.ClientUri,
          LogoUri = configured.LogoUri,
          RedirectUris = redirectJson,
          IsCimd = false,
          CreatedAt = DateTime.UtcNow,
        });
      }
      else
      {
        existing.ClientName = configured.ClientName;
        existing.ClientUri = configured.ClientUri;
        existing.LogoUri = configured.LogoUri;
        existing.RedirectUris = redirectJson;
        existing.IsCimd = false;
        existing.CimdFetchedAt = null;
      }
    }
    await ctx.SaveChangesAsync(ct);
  }

  // ---------------------------------------------------------------------------------------------
  // Refresh tokens (separate storage from first-party tokens)
  // ---------------------------------------------------------------------------------------------

  private async Task<string> IssueRefreshTokenAsync(
    Data.BuoyAIDbContext ctx, int userId, string clientId, string scope, string? resource, CancellationToken ct)
  {
    var raw = GenerateOpaqueToken();
    ctx.OAuthRefreshTokens.Add(new OAuthRefreshToken
    {
      Id = Guid.NewGuid(),
      UserId = userId,
      ClientId = clientId,
      TokenHash = Hash(raw),
      Scope = scope,
      Resource = resource,
      ExpiresAt = DateTime.UtcNow.Add(RefreshTokenTtl),
      CreatedAt = DateTime.UtcNow,
    });
    await ctx.SaveChangesAsync(ct);
    return raw;
  }

  private async Task<RotatedToken?> RotateRefreshTokenAsync(
    Data.BuoyAIDbContext ctx, string rawToken, string? presentedClientId, CancellationToken ct)
  {
    var hash = Hash(rawToken);
    var existing = await ctx.OAuthRefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
    if (existing == null)
      return null;

    // Client binding (RFC 6749 §6): a client_id presented on the refresh grant must match the
    // client the token was issued to. Reject an impostor early — BEFORE reuse-detection or any
    // mutation — so a wrong-client attempt never rotates or burns the real client's token.
    if (!string.IsNullOrWhiteSpace(presentedClientId)
        && !string.Equals(presentedClientId, existing.ClientId, StringComparison.Ordinal))
    {
      _logger.LogWarning(
        "OAuth refresh presented with mismatched client_id (token client {ClientId}); rejected without rotation.",
        existing.ClientId);
      return null;
    }

    // Reuse of an already-rotated (revoked) token → replay/theft. Revoke the whole rotation chain
    // so neither the attacker nor the victim can keep using the family.
    if (existing.RevokedAt != null)
    {
      await RevokeChainAsync(ctx, existing, ct);
      _logger.LogWarning(
        "OAuth refresh-token reuse detected for client {ClientId}, user {UserId}; revoked token family.",
        existing.ClientId, existing.UserId);
      return null;
    }

    if (DateTime.UtcNow >= existing.ExpiresAt)
      return null;

    var raw = GenerateOpaqueToken();
    var replacement = new OAuthRefreshToken
    {
      Id = Guid.NewGuid(),
      UserId = existing.UserId,
      ClientId = existing.ClientId,
      TokenHash = Hash(raw),
      Scope = existing.Scope,
      Resource = existing.Resource,
      ExpiresAt = DateTime.UtcNow.Add(RefreshTokenTtl),
      CreatedAt = DateTime.UtcNow,
    };
    ctx.OAuthRefreshTokens.Add(replacement);

    existing.RevokedAt = DateTime.UtcNow;
    existing.ReplacedBy = replacement.Id;
    await ctx.SaveChangesAsync(ct);

    return new RotatedToken(existing.UserId, existing.ClientId, existing.Scope, existing.Resource, raw);
  }

  /// <summary>Walks the rotation chain forward from a reused token, revoking every descendant.</summary>
  private async Task RevokeChainAsync(Data.BuoyAIDbContext ctx, OAuthRefreshToken start, CancellationToken ct)
  {
    var now = DateTime.UtcNow;
    var cursor = start;
    var guard = 0;
    while (cursor != null && guard++ < 1000)
    {
      if (cursor.RevokedAt == null)
        cursor.RevokedAt = now;
      if (cursor.ReplacedBy == null)
        break;
      var nextId = cursor.ReplacedBy.Value;
      cursor = await ctx.OAuthRefreshTokens.FirstOrDefaultAsync(t => t.Id == nextId, ct);
    }
    await ctx.SaveChangesAsync(ct);
  }

  private static async Task RevokeGrantRefreshTokensAsync(
    Data.BuoyAIDbContext ctx, int userId, string clientId, CancellationToken ct)
  {
    var now = DateTime.UtcNow;
    var active = await ctx.OAuthRefreshTokens
      .Where(t => t.UserId == userId && t.ClientId == clientId && t.RevokedAt == null)
      .ToListAsync(ct);
    foreach (var token in active)
      token.RevokedAt = now;
    if (active.Count > 0)
      await ctx.SaveChangesAsync(ct);
  }

  // ---------------------------------------------------------------------------------------------
  // Access-token minting
  // ---------------------------------------------------------------------------------------------

  private async Task<string> MintAccessTokenAsync(int userId, string clientId, string scope, string resource)
  {
    // Tier is resolved fresh at issuance (same source AIController uses), so a token reflects the
    // user's plan at mint time rather than a stale login snapshot.
    var tier = await _entitlements.GetEffectiveTierAsync(userId);

    var claims = new List<Claim>
    {
      new(ClaimTypes.NameIdentifier, userId.ToString()),
      new("scope", scope),
      new("client_id", clientId),
    };
    if (tier.HasValue)
      claims.Add(new Claim("subscription_tier", tier.Value.ToString()));

    var key = Encoding.ASCII.GetBytes(_jwtKey);
    var descriptor = new SecurityTokenDescriptor
    {
      Subject = new ClaimsIdentity(claims),
      Expires = DateTime.UtcNow.AddSeconds(AccessTokenSeconds),
      SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature),
      // MCP-pinned iss/aud — a Connect token is NEVER valid for the first-party API (which validates
      // ValidIssuer="BuoyAI" / ValidAudience="BuoyAI-Users").
      Issuer = Issuer,
      Audience = resource,
    };

    var handler = new JwtSecurityTokenHandler();
    return handler.WriteToken(handler.CreateToken(descriptor));
  }

  // ---------------------------------------------------------------------------------------------
  // Helpers
  // ---------------------------------------------------------------------------------------------

  /// <summary>PKCE S256: BASE64URL(SHA256(ASCII(code_verifier))) must equal the stored challenge.</summary>
  private static bool VerifyPkce(string codeVerifier, string codeChallenge)
  {
    var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
    var computed = Base64UrlEncode(hash);
    var a = Encoding.ASCII.GetBytes(computed);
    var b = Encoding.ASCII.GetBytes(codeChallenge);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
  }

  private static string GenerateOpaqueToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

  private static string Base64UrlEncode(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

  private static string Hash(string value)
  {
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
    return Convert.ToHexString(bytes).ToLowerInvariant();
  }

  private static List<string> ParseRedirectUris(string json)
  {
    try
    {
      return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
    }
    catch
    {
      return new List<string>();
    }
  }

  private static string BuildSuccessRedirect(string redirectUri, string code, string? state) =>
    AppendQuery(redirectUri, new[]
    {
      new KeyValuePair<string, string?>("code", code),
      new KeyValuePair<string, string?>("state", state),
    });

  private static string BuildErrorRedirect(string redirectUri, string error, string? state) =>
    AppendQuery(redirectUri, new[]
    {
      new KeyValuePair<string, string?>("error", error),
      new KeyValuePair<string, string?>("state", state),
    });

  /// <summary>Appends query params, preserving any existing query and passing values (e.g. state)
  /// through unchanged apart from RFC 3986 percent-encoding for transport.</summary>
  private static string AppendQuery(string uri, IEnumerable<KeyValuePair<string, string?>> parameters)
  {
    var separator = uri.Contains('?') ? '&' : '?';
    var sb = new StringBuilder(uri);
    var first = true;
    foreach (var kv in parameters)
    {
      if (kv.Value == null)
        continue;
      sb.Append(first ? separator : '&');
      first = false;
      sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
    }
    return sb.ToString();
  }

  private sealed record RotatedToken(int UserId, string ClientId, string Scope, string? Resource, string RawToken);

  private sealed class CimdMetadata
  {
    public string? ClientName { get; init; }
    public string? ClientUri { get; init; }
    public string? LogoUri { get; init; }
    public List<string> RedirectUris { get; init; } = new();
  }
}
