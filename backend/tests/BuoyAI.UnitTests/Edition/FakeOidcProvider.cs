using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace BuoyAI.UnitTests.EditionSetting;

/// <summary>
/// A stand-in OpenID Connect provider (like Pocket ID) behind a stub HttpMessageHandler: discovery,
/// JWKS with a real RSA key, and a token endpoint that signs an ID token from the settings below.
/// The nonce is taken from the last authorize URL the test hands it, unless overridden.
/// </summary>
public sealed class FakeOidcProvider : HttpMessageHandler
{
  public const string Issuer = "https://id.home.test";
  public const string ClientId = "buoy-money";
  public const string ClientSecret = "provider-secret";

  private readonly RSA _rsa = RSA.Create(2048);
  private readonly RsaSecurityKey _key;

  public FakeOidcProvider()
  {
    _key = new RsaSecurityKey(_rsa) { KeyId = "k1" };
  }

  public string Subject { get; set; } = "pocket-user-1";
  public string? Email { get; set; } = "owner@home.test";
  /// <summary>null leaves the claim out.</summary>
  public bool? EmailVerified { get; set; } = true;
  public string? GivenName { get; set; } = "Pat";
  public string? FamilyName { get; set; } = "Owner";

  /// <summary>Overrides for the negative tests.</summary>
  public string? NonceOverride { get; set; }
  public string? AudienceOverride { get; set; }
  public string? IssuerOverride { get; set; }

  /// <summary>Set from the authorize URL by <see cref="Remember"/>.</summary>
  public string? LastNonce { get; private set; }

  public int TokenRequests { get; private set; }
  public string? LastTokenAuthorization { get; private set; }
  public string? LastTokenForm { get; private set; }

  /// <summary>Reads state and nonce off an authorize URL, like a browser handing it to the provider.</summary>
  public string Remember(string authorizeUrl)
  {
    var query = ParseQuery(new Uri(authorizeUrl).Query);
    LastNonce = query["nonce"];
    return query["state"];
  }

  public static Dictionary<string, string> ParseQuery(string query) =>
    query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
      .Select(p => p.Split('=', 2))
      .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");

  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    var path = request.RequestUri!.AbsolutePath;
    if (path == "/.well-known/openid-configuration")
    {
      return JsonResponse(new
      {
        issuer = Issuer,
        authorization_endpoint = $"{Issuer}/authorize",
        token_endpoint = $"{Issuer}/api/oidc/token",
        jwks_uri = $"{Issuer}/.well-known/jwks.json",
        userinfo_endpoint = $"{Issuer}/api/oidc/userinfo",
        token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post" },
      });
    }
    if (path == "/.well-known/jwks.json")
    {
      var p = _rsa.ExportParameters(false);
      return JsonResponse(new
      {
        keys = new[]
        {
          new { kty = "RSA", use = "sig", alg = "RS256", kid = "k1", n = Base64Url(p.Modulus!), e = Base64Url(p.Exponent!) }
        }
      });
    }
    if (path == "/api/oidc/token")
    {
      TokenRequests++;
      LastTokenAuthorization = request.Headers.Authorization?.ToString();
      LastTokenForm = await request.Content!.ReadAsStringAsync(cancellationToken);
      return JsonResponse(new { access_token = "at", token_type = "Bearer", id_token = IdToken() });
    }
    return new HttpResponseMessage(HttpStatusCode.NotFound);
  }

  private string IdToken()
  {
    var claims = new List<Claim>
    {
      new("sub", Subject),
      new("nonce", NonceOverride ?? LastNonce ?? "none"),
    };
    if (Email != null) claims.Add(new Claim("email", Email));
    if (EmailVerified != null)
      claims.Add(new Claim("email_verified", EmailVerified.Value ? "true" : "false", ClaimValueTypes.Boolean));
    if (GivenName != null) claims.Add(new Claim("given_name", GivenName));
    if (FamilyName != null) claims.Add(new Claim("family_name", FamilyName));

    var now = DateTime.UtcNow;
    var token = new JwtSecurityToken(
      issuer: IssuerOverride ?? Issuer,
      audience: AudienceOverride ?? ClientId,
      claims: claims,
      notBefore: now.AddMinutes(-1),
      expires: now.AddMinutes(5),
      signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.RsaSha256));
    return new JwtSecurityTokenHandler().WriteToken(token);
  }

  private static HttpResponseMessage JsonResponse(object body) => new(HttpStatusCode.OK)
  {
    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
  };

  private static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

  protected override void Dispose(bool disposing)
  {
    if (disposing) _rsa.Dispose();
    base.Dispose(disposing);
  }
}
