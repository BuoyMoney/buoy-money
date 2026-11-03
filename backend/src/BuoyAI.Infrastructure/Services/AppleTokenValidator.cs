using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Verifies Apple "Sign in with Apple" identity tokens against Apple's published
/// JWKS. Validates the JWT signature, issuer (<c>https://appleid.apple.com</c>),
/// audience (our configured client/bundle IDs), and expiry before returning any
/// claims. Registered as a singleton so the fetched signing keys are cached
/// across requests.
/// </summary>
public class AppleTokenValidator : IAppleTokenValidator
{
  private const string DefaultIssuer = "https://appleid.apple.com";
  private const string DefaultJwksUri = "https://appleid.apple.com/auth/keys";

  /// <summary>
  /// The app's Apple audience: the native iOS bundle ID (see the committed Xcode
  /// project). A public identifier, not a secret. Baked in as the default so a
  /// deploy that ships no <c>AppleAuth</c> config still pins the audience; override
  /// <c>AppleAuth:ClientIds</c> to add a web "Services ID" or additional bundle IDs.
  /// </summary>
  internal const string DefaultClientId = "com.buoyai.buoyAi";

  private static readonly TimeSpan KeyCacheTtl = TimeSpan.FromHours(6);

  private readonly IHttpClientFactory _httpClientFactory;
  private readonly ILogger<AppleTokenValidator> _logger;
  private readonly IReadOnlyList<string> _allowedAudiences;
  private readonly string _issuer;
  private readonly string _jwksUri;

  private readonly SemaphoreSlim _refreshLock = new(1, 1);
  private JsonWebKeySet? _cachedKeys;
  private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;

  public AppleTokenValidator(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<AppleTokenValidator> logger)
  {
    _httpClientFactory = httpClientFactory;
    _logger = logger;

    // Allowed audiences are the app's Apple client IDs: the native iOS bundle ID
    // and/or the web "Services ID". A token whose `aud` is outside this set is
    // rejected. Falls back to the public bundle ID so validation is always pinned.
    var configuredIds = configuration.GetSection("AppleAuth:ClientIds").Get<string[]>()
      ?.Where(a => !string.IsNullOrWhiteSpace(a))
      .ToArray();
    _allowedAudiences = configuredIds is { Length: > 0 } ? configuredIds : new[] { DefaultClientId };

    _issuer = configuration["AppleAuth:Issuer"] ?? DefaultIssuer;
    _jwksUri = configuration["AppleAuth:JwksUri"] ?? DefaultJwksUri;
  }

  public async Task<AppleTokenPayload?> ValidateAsync(string identityToken, string? expectedNonce = null)
  {
    if (string.IsNullOrWhiteSpace(identityToken))
      return null;

    IEnumerable<SecurityKey> signingKeys;
    try
    {
      signingKeys = await GetSigningKeysAsync();
    }
    catch (Exception ex)
    {
      // Never fall through to an unverified path when Apple's keys are unreachable.
      _logger.LogError(ex, "Failed to retrieve Apple JWKS; rejecting token.");
      return null;
    }

    var parameters = new TokenValidationParameters
    {
      ValidateIssuer = true,
      ValidIssuer = _issuer,
      ValidateAudience = true,
      ValidAudiences = _allowedAudiences,
      ValidateLifetime = true,
      ValidateIssuerSigningKey = true,
      IssuerSigningKeys = signingKeys,
      ClockSkew = TimeSpan.FromMinutes(2)
    };

    var handler = new JwtSecurityTokenHandler();
    JwtSecurityToken jwt;
    try
    {
      handler.ValidateToken(identityToken, parameters, out var validated);
      jwt = (JwtSecurityToken)validated;
    }
    catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
    {
      _logger.LogWarning(ex, "Apple identity token failed validation.");
      return null;
    }

    // Bind the token to the sign-in request's nonce when one was supplied,
    // defeating replay of a token captured from another session.
    if (!string.IsNullOrEmpty(expectedNonce))
    {
      var tokenNonce = jwt.Claims.FirstOrDefault(c => c.Type == "nonce")?.Value;
      if (!NonceMatches(expectedNonce, tokenNonce))
      {
        _logger.LogWarning("Apple identity token nonce did not match the expected value.");
        return null;
      }
    }

    var subject = jwt.Subject;
    if (string.IsNullOrEmpty(subject))
      return null;

    var email = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value;
    var emailVerified = string.Equals(
      jwt.Claims.FirstOrDefault(c => c.Type == "email_verified")?.Value,
      "true",
      StringComparison.OrdinalIgnoreCase);

    return new AppleTokenPayload
    {
      Subject = subject,
      Email = email,
      EmailVerified = emailVerified
    };
  }

  public async Task<AppleServerNotification?> ValidateServerNotificationAsync(string signedPayload)
  {
    if (string.IsNullOrWhiteSpace(signedPayload))
      return null;

    IEnumerable<SecurityKey> signingKeys;
    try
    {
      signingKeys = await GetSigningKeysAsync();
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to retrieve Apple JWKS; rejecting notification.");
      return null;
    }

    var parameters = new TokenValidationParameters
    {
      ValidateIssuer = true,
      ValidIssuer = _issuer,
      ValidateAudience = true,
      ValidAudiences = _allowedAudiences,
      ValidateLifetime = false, // server notifications carry no exp claim
      ValidateIssuerSigningKey = true,
      IssuerSigningKeys = signingKeys,
      ClockSkew = TimeSpan.FromMinutes(2)
    };

    var handler = new JwtSecurityTokenHandler();
    JwtSecurityToken jwt;
    try
    {
      handler.ValidateToken(signedPayload, parameters, out var validated);
      jwt = (JwtSecurityToken)validated;
    }
    catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
    {
      _logger.LogWarning(ex, "Apple server notification failed validation.");
      return null;
    }

    // The `events` claim is a JSON-encoded string: { type, sub, email?, event_time }.
    var eventsJson = jwt.Claims.FirstOrDefault(c => c.Type == "events")?.Value;
    if (string.IsNullOrWhiteSpace(eventsJson))
    {
      _logger.LogWarning("Apple server notification had no events claim.");
      return null;
    }

    try
    {
      using var doc = JsonDocument.Parse(eventsJson);
      var root = doc.RootElement;
      return new AppleServerNotification
      {
        Type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? string.Empty) : string.Empty,
        Subject = root.TryGetProperty("sub", out var s) ? (s.GetString() ?? string.Empty) : string.Empty,
        Email = root.TryGetProperty("email", out var e) ? e.GetString() : null
      };
    }
    catch (JsonException ex)
    {
      _logger.LogWarning(ex, "Apple notification events claim was not valid JSON.");
      return null;
    }
  }

  /// <summary>
  /// Apple sets the token's <c>nonce</c> claim to the value the client supplied at
  /// authorization time. Clients commonly pass the SHA-256 of a raw nonce, so accept
  /// either the raw value or its SHA-256 hex to verify across both conventions.
  /// </summary>
  private static bool NonceMatches(string expectedNonce, string? tokenNonce)
  {
    if (string.IsNullOrEmpty(tokenNonce))
      return false;
    if (string.Equals(expectedNonce, tokenNonce, StringComparison.Ordinal))
      return true;
    return string.Equals(Sha256Hex(expectedNonce), tokenNonce, StringComparison.OrdinalIgnoreCase);
  }

  private static string Sha256Hex(string input)
  {
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(hash).ToLowerInvariant();
  }

  /// <summary>
  /// Fetches (and caches) Apple's JSON Web Key Set. Overridable so tests can supply
  /// signing keys without network access.
  /// </summary>
  protected virtual async Task<IEnumerable<SecurityKey>> GetSigningKeysAsync()
  {
    if (_cachedKeys != null && DateTimeOffset.UtcNow < _cacheExpiresAt)
      return _cachedKeys.GetSigningKeys();

    await _refreshLock.WaitAsync();
    try
    {
      if (_cachedKeys != null && DateTimeOffset.UtcNow < _cacheExpiresAt)
        return _cachedKeys.GetSigningKeys();

      var client = _httpClientFactory.CreateClient();
      var json = await client.GetStringAsync(_jwksUri);
      var keys = new JsonWebKeySet(json);
      _cachedKeys = keys;
      _cacheExpiresAt = DateTimeOffset.UtcNow.Add(KeyCacheTtl);
      return keys.GetSigningKeys();
    }
    finally
    {
      _refreshLock.Release();
    }
  }
}
