using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Exceptions;
using BuoyAI.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace BuoyAI.Infrastructure.Services.Oidc;

/// <summary>The parts of a provider's discovery document the sign-in flow uses.</summary>
public sealed record OidcDiscovery(
  string Issuer,
  string AuthorizationEndpoint,
  string TokenEndpoint,
  string JwksUri,
  string? UserInfoEndpoint,
  IReadOnlyList<string> TokenEndpointAuthMethods);

/// <summary>
/// Talks to an OpenID Connect provider: reads its discovery document, builds the authorize URL,
/// trades the code at the token endpoint and verifies the ID token (signature from the provider's
/// JWKS, issuer, audience, expiry, nonce). Singleton, so discovery and keys are cached for an hour
/// (keys are fetched again at once when a token names a key we do not have, after a key rotation).
/// </summary>
public class OidcProviderClient
{
  public const string HttpClientName = "oidc";

  private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

  /// <summary>Asymmetric algorithms only: an HMAC-signed or unsigned ID token is never accepted.</summary>
  private static readonly string[] AllowedAlgorithms =
  {
    SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
    SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
    SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
  };

  private readonly IHttpClientFactory _httpClientFactory;
  private readonly ILogger<OidcProviderClient> _logger;
  private readonly SemaphoreSlim _lock = new(1, 1);

  private string? _cachedAuthority;
  private OidcDiscovery? _discovery;
  private DateTimeOffset _discoveryExpires = DateTimeOffset.MinValue;
  private IReadOnlyList<SecurityKey>? _keys;
  private DateTimeOffset _keysExpires = DateTimeOffset.MinValue;

  public OidcProviderClient(IHttpClientFactory httpClientFactory, ILogger<OidcProviderClient> logger)
  {
    _httpClientFactory = httpClientFactory;
    _logger = logger;
  }

  public async Task<OidcDiscovery> GetDiscoveryAsync(OidcOptions options, CancellationToken ct = default)
  {
    var authority = options.Authority!.TrimEnd('/');
    await _lock.WaitAsync(ct);
    try
    {
      if (_cachedAuthority == authority && _discovery != null && DateTimeOffset.UtcNow < _discoveryExpires)
        return _discovery;

      var url = $"{authority}/.well-known/openid-configuration";
      using var doc = await GetJsonAsync(url, ct);
      var root = doc.RootElement;
      string Required(string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
          ? v.GetString()!
          : throw new OidcLoginException("provider_unavailable", $"The sign-in provider settings are missing {name}.");

      var issuer = Required("issuer");
      // The discovery document must belong to the configured provider (OpenID Connect Discovery 4.3).
      if (!string.Equals(issuer.TrimEnd('/'), authority, StringComparison.Ordinal))
      {
        _logger.LogError("OIDC discovery issuer {Issuer} does not match Oidc:Authority {Authority}", issuer, authority);
        throw new OidcLoginException("provider_unavailable",
          "The sign-in provider issuer does not match the address this server is set up with.");
      }

      var methods = new List<string>();
      if (root.TryGetProperty("token_endpoint_auth_methods_supported", out var m) && m.ValueKind == JsonValueKind.Array)
        methods.AddRange(m.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));

      var discovery = new OidcDiscovery(
        issuer,
        Required("authorization_endpoint"),
        Required("token_endpoint"),
        Required("jwks_uri"),
        root.TryGetProperty("userinfo_endpoint", out var ui) && ui.ValueKind == JsonValueKind.String ? ui.GetString() : null,
        methods);

      if (_cachedAuthority != authority)
      {
        _keys = null;
        _keysExpires = DateTimeOffset.MinValue;
      }
      _cachedAuthority = authority;
      _discovery = discovery;
      _discoveryExpires = DateTimeOffset.UtcNow.Add(CacheTtl);
      return discovery;
    }
    finally
    {
      _lock.Release();
    }
  }

  /// <summary>The provider's authorize URL for one sign-in (code flow, PKCE S256).</summary>
  public async Task<string> BuildAuthorizationUrlAsync(
    OidcOptions options, string redirectUri, string state, string nonce, string codeChallenge, CancellationToken ct = default)
  {
    var discovery = await GetDiscoveryAsync(options, ct);
    var query = new Dictionary<string, string>
    {
      ["response_type"] = "code",
      ["client_id"] = options.ClientId!,
      ["redirect_uri"] = redirectUri,
      ["scope"] = "openid email profile",
      ["state"] = state,
      ["nonce"] = nonce,
      ["code_challenge"] = codeChallenge,
      ["code_challenge_method"] = "S256",
    };
    var separator = discovery.AuthorizationEndpoint.Contains('?') ? "&" : "?";
    return discovery.AuthorizationEndpoint + separator +
      string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
  }

  /// <summary>
  /// Trades the authorization code for tokens and verifies the ID token. Throws
  /// <see cref="OidcLoginException"/> on any failure; never returns an unverified identity.
  /// </summary>
  public async Task<OidcIdentity> RedeemCodeAsync(
    OidcOptions options, string redirectUri, string code, string codeVerifier, string expectedNonce, CancellationToken ct = default)
  {
    var discovery = await GetDiscoveryAsync(options, ct);

    var form = new Dictionary<string, string>
    {
      ["grant_type"] = "authorization_code",
      ["code"] = code,
      ["redirect_uri"] = redirectUri,
      ["code_verifier"] = codeVerifier,
    };
    using var request = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint);
    if (!string.IsNullOrEmpty(options.ClientSecret) && UseBasicAuth(discovery))
    {
      // RFC 6749 2.3.1: form-encode both parts before joining them.
      var raw = $"{Uri.EscapeDataString(options.ClientId!)}:{Uri.EscapeDataString(options.ClientSecret)}";
      request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
    }
    else
    {
      form["client_id"] = options.ClientId!;
      if (!string.IsNullOrEmpty(options.ClientSecret)) form["client_secret"] = options.ClientSecret;
    }
    request.Content = new FormUrlEncodedContent(form);
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    string idToken;
    string? accessToken;
    try
    {
      using var response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
      var body = await response.Content.ReadAsStringAsync(ct);
      if (!response.IsSuccessStatusCode)
      {
        _logger.LogWarning("OIDC token endpoint answered {Status}: {Body}", (int)response.StatusCode, Truncate(body));
        throw new OidcLoginException("invalid_response", "The sign-in provider refused the sign-in. Please try again.");
      }
      using var doc = JsonDocument.Parse(body);
      idToken = doc.RootElement.TryGetProperty("id_token", out var t) && t.ValueKind == JsonValueKind.String
        ? t.GetString()!
        : throw new OidcLoginException("invalid_response", "The sign-in provider sent no ID token.");
      accessToken = doc.RootElement.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String
        ? a.GetString()
        : null;
    }
    catch (OidcLoginException)
    {
      throw;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
      _logger.LogWarning(ex, "OIDC token exchange failed");
      throw new OidcLoginException("provider_unavailable", "The sign-in provider could not be reached.", ex);
    }

    var jwt = await ValidateIdTokenAsync(options, discovery, idToken, ct);

    var nonce = jwt.Claims.FirstOrDefault(c => c.Type == "nonce")?.Value;
    if (nonce == null || !FixedTimeEquals(nonce, expectedNonce))
    {
      _logger.LogWarning("OIDC ID token nonce did not match the sign-in request");
      throw new OidcLoginException("invalid_response", "The sign-in answer did not match this sign-in. Please try again.");
    }

    // With more than one audience, the token must name us as the party it was issued to (OIDC Core 3.1.3.7).
    var audiences = jwt.Audiences.ToList();
    var azp = jwt.Claims.FirstOrDefault(c => c.Type == "azp")?.Value;
    if (audiences.Count > 1 && azp != options.ClientId)
    {
      throw new OidcLoginException("invalid_response", "The sign-in answer was meant for another app.");
    }

    string? Claim(string type) => jwt.Claims.FirstOrDefault(c => c.Type == type)?.Value;
    var subject = jwt.Subject;
    if (string.IsNullOrWhiteSpace(subject))
      throw new OidcLoginException("invalid_response", "The sign-in answer had no user id.");

    var email = Claim("email");
    var verifiedRaw = Claim("email_verified");
    string? given = Claim("given_name"), family = Claim("family_name"), name = Claim("name");

    // Some providers leave profile claims out of the ID token; ask the userinfo endpoint then.
    if (string.IsNullOrWhiteSpace(email) && discovery.UserInfoEndpoint != null && accessToken != null)
    {
      var info = await GetUserInfoAsync(discovery.UserInfoEndpoint, accessToken, ct);
      if (info != null && info.TryGetValue("sub", out var infoSub) && infoSub == subject)
      {
        info.TryGetValue("email", out email);
        info.TryGetValue("email_verified", out verifiedRaw);
        if (given == null) info.TryGetValue("given_name", out given);
        if (family == null) info.TryGetValue("family_name", out family);
        if (name == null) info.TryGetValue("name", out name);
      }
    }

    var verified = string.Equals(verifiedRaw, "true", StringComparison.OrdinalIgnoreCase);
    return new OidcIdentity(jwt.Issuer, subject, email, verified, given, family, name);
  }

  private async Task<JwtSecurityToken> ValidateIdTokenAsync(
    OidcOptions options, OidcDiscovery discovery, string idToken, CancellationToken ct)
  {
    var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
    for (var attempt = 0; attempt < 2; attempt++)
    {
      var keys = await GetSigningKeysAsync(discovery, forceRefresh: attempt > 0, ct);
      var parameters = new TokenValidationParameters
      {
        ValidateIssuer = true,
        ValidIssuer = discovery.Issuer,
        ValidateAudience = true,
        ValidAudience = options.ClientId,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKeys = keys,
        ValidAlgorithms = AllowedAlgorithms,
        ClockSkew = TimeSpan.FromMinutes(2),
      };
      try
      {
        handler.ValidateToken(idToken, parameters, out var validated);
        return (JwtSecurityToken)validated;
      }
      catch (SecurityTokenSignatureKeyNotFoundException) when (attempt == 0)
      {
        // The provider may have rotated its keys since we cached them: fetch once more.
        continue;
      }
      catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
      {
        _logger.LogWarning(ex, "OIDC ID token failed validation");
        throw new OidcLoginException("invalid_response", "The sign-in answer could not be verified.");
      }
    }
    throw new OidcLoginException("invalid_response", "The sign-in answer could not be verified.");
  }

  private async Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(OidcDiscovery discovery, bool forceRefresh, CancellationToken ct)
  {
    await _lock.WaitAsync(ct);
    try
    {
      if (!forceRefresh && _keys != null && DateTimeOffset.UtcNow < _keysExpires) return _keys;
      try
      {
        using var response = await _httpClientFactory.CreateClient(HttpClientName).GetAsync(discovery.JwksUri, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        _keys = new JsonWebKeySet(json).GetSigningKeys().ToList();
        _keysExpires = DateTimeOffset.UtcNow.Add(CacheTtl);
        return _keys;
      }
      catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or JsonException)
      {
        _logger.LogWarning(ex, "Could not load the OIDC provider signing keys");
        throw new OidcLoginException("provider_unavailable", "The sign-in provider could not be reached.", ex);
      }
    }
    finally
    {
      _lock.Release();
    }
  }

  private async Task<Dictionary<string, string>?> GetUserInfoAsync(string endpoint, string accessToken, CancellationToken ct)
  {
    try
    {
      using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
      using var response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
      if (!response.IsSuccessStatusCode) return null;
      using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
      var result = new Dictionary<string, string>();
      foreach (var p in doc.RootElement.EnumerateObject())
      {
        result[p.Name] = p.Value.ValueKind switch
        {
          JsonValueKind.String => p.Value.GetString()!,
          JsonValueKind.True => "true",
          JsonValueKind.False => "false",
          _ => p.Value.GetRawText(),
        };
      }
      return result;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
      _logger.LogWarning(ex, "OIDC userinfo request failed");
      return null;
    }
  }

  private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
  {
    try
    {
      using var response = await _httpClientFactory.CreateClient(HttpClientName).GetAsync(url, ct);
      response.EnsureSuccessStatusCode();
      return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
      _logger.LogWarning(ex, "Could not read OIDC discovery at {Url}", url);
      throw new OidcLoginException("provider_unavailable", "The sign-in provider could not be reached.", ex);
    }
  }

  /// <summary>client_secret_basic is the spec default; use client_secret_post only when the provider lists it and not basic.</summary>
  private static bool UseBasicAuth(OidcDiscovery discovery) =>
    discovery.TokenEndpointAuthMethods.Count == 0 ||
    discovery.TokenEndpointAuthMethods.Contains("client_secret_basic") ||
    !discovery.TokenEndpointAuthMethods.Contains("client_secret_post");

  private static bool FixedTimeEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

  private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
