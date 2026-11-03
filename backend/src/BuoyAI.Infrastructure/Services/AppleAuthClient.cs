using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services;

/// <inheritdoc cref="IAppleAuthClient"/>
public class AppleAuthClient : IAppleAuthClient
{
    private const string AppleAudience = "https://appleid.apple.com";
    private const string TokenEndpoint = "https://appleid.apple.com/auth/token";
    private const string RevokeEndpoint = "https://appleid.apple.com/auth/revoke";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppleAuthClient> _logger;
    private readonly string? _teamId;
    private readonly string? _keyId;
    private readonly string? _clientId;
    private readonly string? _privateKeyPem;

    public AppleAuthClient(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AppleAuthClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _teamId = configuration["AppleAuth:TeamId"];
        _keyId = configuration["AppleAuth:KeyId"];
        // The client_id for NATIVE Sign in with Apple is the app's bundle ID; for WEB it's
        // the Services ID. Defaults to the bundle ID (native), the only flow wired today.
        _clientId = configuration["AppleAuth:ClientId"] ?? "com.buoyai.buoyAi";
        _privateKeyPem = ResolvePrivateKey(configuration);
    }

    /// <summary>
    /// The <c>.p8</c> private key, either inline (prod env <c>AppleAuth__PrivateKey</c>) or
    /// from a file path (local dev <c>AppleAuth:PrivateKeyPath</c>). Single-line env vars
    /// commonly store newlines as literal "\n", so those are normalized.
    /// </summary>
    private static string? ResolvePrivateKey(IConfiguration configuration)
    {
        var pem = configuration["AppleAuth:PrivateKey"];
        if (!string.IsNullOrWhiteSpace(pem))
        {
            return pem.Replace("\\n", "\n");
        }

        var path = configuration["AppleAuth:PrivateKeyPath"];
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return File.ReadAllText(path);
        }

        return null;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_teamId) &&
        !string.IsNullOrWhiteSpace(_keyId) &&
        !string.IsNullOrWhiteSpace(_clientId) &&
        !string.IsNullOrWhiteSpace(_privateKeyPem);

    public string GenerateClientSecret()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Apple auth is not configured (needs AppleAuth:TeamId, KeyId, ClientId, and PrivateKey).");
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(_privateKeyPem!); // .p8 is PKCS#8 ("BEGIN PRIVATE KEY")
        var securityKey = new ECDsaSecurityKey(ecdsa) { KeyId = _keyId };
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.EcdsaSha256);

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: _teamId,
            audience: AppleAudience,
            claims: new[] { new Claim(JwtRegisteredClaimNames.Sub, _clientId!) },
            notBefore: now,
            // Apple permits up to 6 months; short-lived is fine since we mint one per call.
            expires: now.AddMinutes(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<string?> ExchangeAuthorizationCodeAsync(
        string authorizationCode, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(authorizationCode))
        {
            return null;
        }

        try
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _clientId!,
                ["client_secret"] = GenerateClientSecret(),
                ["grant_type"] = "authorization_code",
                ["code"] = authorizationCode,
            };

            var http = _httpClientFactory.CreateClient();
            using var resp = await http.PostAsync(
                TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Apple token exchange failed ({Status}): {Body}", (int)resp.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apple authorization-code exchange threw");
            return null;
        }
    }

    public async Task<bool> RevokeAsync(
        string token, string tokenTypeHint = "refresh_token", CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _clientId!,
                ["client_secret"] = GenerateClientSecret(),
                ["token"] = token,
                ["token_type_hint"] = tokenTypeHint,
            };

            var http = _httpClientFactory.CreateClient();
            using var resp = await http.PostAsync(
                RevokeEndpoint, new FormUrlEncodedContent(form), cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Apple token revoke failed ({Status}): {Body}", (int)resp.StatusCode, body);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apple token revoke threw");
            return false;
        }
    }
}
