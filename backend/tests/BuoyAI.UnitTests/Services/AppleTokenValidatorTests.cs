using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Verifies Apple identity-token validation. A test RSA key stands in for Apple's
/// JWKS (injected via <see cref="TestableAppleTokenValidator"/>) so we can assert
/// real reject-on-bad-signature / audience / issuer / expiry behavior offline.
/// </summary>
public class AppleTokenValidatorTests
{
  private const string Issuer = "https://appleid.apple.com";
  private const string Audience = "com.buoyai.buoyAi";

  private static readonly RSA SigningRsa = RSA.Create(2048);
  private static readonly RsaSecurityKey SigningKey = new(SigningRsa) { KeyId = "test-key-1" };

  // Overrides the JWKS fetch so validation runs against our test key, not Apple's.
  private sealed class TestableAppleTokenValidator : AppleTokenValidator
  {
    private readonly IEnumerable<SecurityKey> _keys;

    public TestableAppleTokenValidator(IConfiguration config, IEnumerable<SecurityKey> keys)
      : base(Mock.Of<IHttpClientFactory>(), config, NullLogger<AppleTokenValidator>.Instance)
    {
      _keys = keys;
    }

    protected override Task<IEnumerable<SecurityKey>> GetSigningKeysAsync() => Task.FromResult(_keys);
  }

  private static IConfiguration BuildConfig(params string[] clientIds)
  {
    var dict = new Dictionary<string, string?>();
    for (var i = 0; i < clientIds.Length; i++)
      dict[$"AppleAuth:ClientIds:{i}"] = clientIds[i];
    return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
  }

  private static TestableAppleTokenValidator Validator(params string[] clientIds)
  {
    var ids = clientIds.Length == 0 ? new[] { Audience } : clientIds;
    return new TestableAppleTokenValidator(BuildConfig(ids), new SecurityKey[] { SigningKey });
  }

  private static string CreateToken(
    SecurityKey signingKey,
    string issuer = Issuer,
    string audience = Audience,
    string sub = "apple-user-123",
    string? email = "user@example.com",
    DateTime? expires = null,
    string? nonce = null)
  {
    var exp = expires ?? DateTime.UtcNow.AddMinutes(10);
    var claims = new List<Claim> { new("sub", sub) };
    if (email != null)
    {
      claims.Add(new Claim("email", email));
      claims.Add(new Claim("email_verified", "true"));
    }
    if (nonce != null)
      claims.Add(new Claim("nonce", nonce));

    var token = new JwtSecurityToken(
      issuer: issuer,
      audience: audience,
      claims: claims,
      notBefore: exp.AddMinutes(-30),
      expires: exp,
      signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));

    return new JwtSecurityTokenHandler().WriteToken(token);
  }

  [Fact]
  public async Task ValidateAsync_ValidToken_ReturnsPayload()
  {
    var token = CreateToken(SigningKey);

    var result = await Validator().ValidateAsync(token);

    result.Should().NotBeNull();
    result!.Subject.Should().Be("apple-user-123");
    result.Email.Should().Be("user@example.com");
    result.EmailVerified.Should().BeTrue();
  }

  [Fact]
  public async Task ValidateAsync_WrongAudience_ReturnsNull()
  {
    var token = CreateToken(SigningKey, audience: "com.someone.else");

    var result = await Validator().ValidateAsync(token);

    result.Should().BeNull();
  }

  [Fact]
  public async Task ValidateAsync_WrongSignature_ReturnsNull()
  {
    using var attackerRsa = RSA.Create(2048);
    var attackerKey = new RsaSecurityKey(attackerRsa) { KeyId = "attacker-key" };
    var token = CreateToken(attackerKey); // not signed by a key in our JWKS

    var result = await Validator().ValidateAsync(token);

    result.Should().BeNull();
  }

  [Fact]
  public async Task ValidateAsync_WrongIssuer_ReturnsNull()
  {
    var token = CreateToken(SigningKey, issuer: "https://evil.example.com");

    var result = await Validator().ValidateAsync(token);

    result.Should().BeNull();
  }

  [Fact]
  public async Task ValidateAsync_ExpiredToken_ReturnsNull()
  {
    var token = CreateToken(SigningKey, expires: DateTime.UtcNow.AddMinutes(-10));

    var result = await Validator().ValidateAsync(token);

    result.Should().BeNull();
  }

  [Fact]
  public async Task ValidateAsync_UsesPublicDefaultBundleId_WhenUnconfigured()
  {
    // With no AppleAuth:ClientIds configured, the public bundle-id default applies,
    // so a token minted for it still validates (a deploy shipping no Apple config
    // stays pinned rather than failing open or closed).
    var validator = new TestableAppleTokenValidator(BuildConfig(), new SecurityKey[] { SigningKey });
    var token = CreateToken(SigningKey, audience: AppleTokenValidator.DefaultClientId);

    var result = await validator.ValidateAsync(token);

    result.Should().NotBeNull();
    result!.Subject.Should().Be("apple-user-123");
  }

  [Fact]
  public async Task ValidateAsync_NonceMismatch_ReturnsNull()
  {
    var token = CreateToken(SigningKey, nonce: "expected-nonce");

    var result = await Validator().ValidateAsync(token, expectedNonce: "different-nonce");

    result.Should().BeNull();
  }

  [Fact]
  public async Task ValidateAsync_NonceMatchesRawValue_ReturnsPayload()
  {
    var token = CreateToken(SigningKey, nonce: "raw-nonce-xyz");

    var result = await Validator().ValidateAsync(token, expectedNonce: "raw-nonce-xyz");

    result.Should().NotBeNull();
  }
}
