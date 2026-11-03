using System.IdentityModel.Tokens.Jwt;
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
/// Verifies the Apple client-secret signer — the security-critical ES256 JWT built from
/// the team's <c>.p8</c> key — using a freshly generated P-256 key so the test needs no
/// real key material, and confirms graceful no-ops when unconfigured.
/// </summary>
public class AppleAuthClientTests
{
    private const string TeamId = "62F9QFZ3UM";
    private const string KeyId = "323K8ZTG44";
    private const string ClientId = "com.buoyai.buoyAi";

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static AppleAuthClient Client(IConfiguration cfg) =>
        new(cfg, Mock.Of<IHttpClientFactory>(), NullLogger<AppleAuthClient>.Instance);

    [Fact]
    public void GenerateClientSecret_ProducesValidEs256Jwt_SignedByTheProvidedKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ecdsa.ExportPkcs8PrivateKeyPem();

        var client = Client(Config(new Dictionary<string, string?>
        {
            ["AppleAuth:TeamId"] = TeamId,
            ["AppleAuth:KeyId"] = KeyId,
            ["AppleAuth:ClientId"] = ClientId,
            ["AppleAuth:PrivateKey"] = pem,
        }));

        client.IsConfigured.Should().BeTrue();
        var jwt = client.GenerateClientSecret();

        // Signature must verify against the key we configured, and header/claims must
        // match exactly what Apple's token/revoke endpoints require.
        var handler = new JwtSecurityTokenHandler();
        handler.ValidateToken(jwt, new TokenValidationParameters
        {
            IssuerSigningKey = new ECDsaSecurityKey(ecdsa),
            ValidateIssuerSigningKey = true,
            ValidIssuer = TeamId,
            ValidateIssuer = true,
            ValidAudience = "https://appleid.apple.com",
            ValidateAudience = true,
            ValidateLifetime = true,
        }, out var validated);

        var token = (JwtSecurityToken)validated;
        token.Header.Alg.Should().Be("ES256");
        token.Header.Kid.Should().Be(KeyId);
        token.Issuer.Should().Be(TeamId);
        token.Audiences.Should().Contain("https://appleid.apple.com");
        token.Subject.Should().Be(ClientId);
    }

    [Fact]
    public void GenerateClientSecret_NormalizesEscapedNewlinesFromEnvVars()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // Simulate a single-line env var where the PEM newlines are literal "\n".
        var escaped = ecdsa.ExportPkcs8PrivateKeyPem().Replace("\n", "\\n");

        var client = Client(Config(new Dictionary<string, string?>
        {
            ["AppleAuth:TeamId"] = TeamId,
            ["AppleAuth:KeyId"] = KeyId,
            ["AppleAuth:ClientId"] = ClientId,
            ["AppleAuth:PrivateKey"] = escaped,
        }));

        client.IsConfigured.Should().BeTrue();
        var act = () => client.GenerateClientSecret();
        act.Should().NotThrow(); // the "\n" must be un-escaped so ImportFromPem parses it
    }

    [Fact]
    public void IsConfigured_False_AndGenerateThrows_WhenKeyMissing()
    {
        var client = Client(Config(new Dictionary<string, string?>
        {
            ["AppleAuth:TeamId"] = TeamId,
            ["AppleAuth:KeyId"] = KeyId,
            // no private key
        }));

        client.IsConfigured.Should().BeFalse();
        client.Invoking(c => c.GenerateClientSecret())
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task ExchangeAndRevoke_NoOpGracefully_WhenUnconfigured()
    {
        var client = Client(Config(new Dictionary<string, string?>()));

        (await client.ExchangeAuthorizationCodeAsync("some-code")).Should().BeNull();
        (await client.RevokeAsync("some-token")).Should().BeFalse();
    }
}
