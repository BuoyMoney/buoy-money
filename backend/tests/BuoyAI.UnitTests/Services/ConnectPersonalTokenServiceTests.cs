using System.IdentityModel.Tokens.Jwt;
using System.Text;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Personal access tokens for BuoyAI Connect: the token is a Connect JWT pinned to the MCP issuer and
/// audience (so it never works on the app's own API), and it stops working once revoked or claimed by
/// another user.
/// </summary>
public class ConnectPersonalTokenServiceTests
{
  private const string Issuer = "https://buoy.example.ts.net:10111";
  private const string ResourceUri = "https://buoy.example.ts.net:10111/mcp";
  private const string Key = "unit-test-jwt-key-0123456789abcdef0123456789abcdef";

  private readonly ConnectPersonalTokenService _service;

  public ConnectPersonalTokenServiceTests()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString())
      .Options;
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = Key })
      .Build();
    _service = new ConnectPersonalTokenService(
      new Factory(options),
      Options.Create(new McpOptions { Enabled = true, Issuer = Issuer, ResourceUri = ResourceUri }),
      config);
  }

  [Fact]
  public async Task Create_ReturnsAConnectTokenForTheMcpAudienceOnly()
  {
    var created = await _service.CreateAsync(7, "Open WebUI", CancellationToken.None);

    created.McpUrl.Should().Be(ResourceUri);
    var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
    var principal = handler.ValidateToken(created.Token, new TokenValidationParameters
    {
      IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(Key)),
      ValidIssuer = Issuer,
      ValidAudience = ResourceUri,
    }, out _);
    principal.FindFirst("nameid")!.Value.Should().Be("7");
    principal.FindFirst("client_id")!.Value.Should().Be(IConnectPersonalTokenService.ClientId);
    principal.FindFirst("jti")!.Value.Should().Be(created.Id.ToString());
    principal.FindFirst("scope")!.Value.Should().Contain("buoyai:read");

    var firstParty = () => handler.ValidateToken(created.Token, new TokenValidationParameters
    {
      IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(Key)),
      ValidIssuer = "BuoyAI",
      ValidAudience = "BuoyAI-Users",
    }, out _);
    firstParty.Should().Throw<SecurityTokenException>();
  }

  [Fact]
  public async Task Validate_FailsAfterRevokeAndForAnotherUser()
  {
    var created = await _service.CreateAsync(7, "Open WebUI", CancellationToken.None);

    (await _service.ValidateAsync(created.Id, 7, CancellationToken.None)).Should().BeTrue();
    (await _service.ValidateAsync(created.Id, 8, CancellationToken.None)).Should().BeFalse();

    (await _service.RevokeAsync(8, created.Id, CancellationToken.None)).Should().BeFalse("only the owner can revoke");
    (await _service.RevokeAsync(7, created.Id, CancellationToken.None)).Should().BeTrue();

    (await _service.ValidateAsync(created.Id, 7, CancellationToken.None)).Should().BeFalse();
    (await _service.ListAsync(7, CancellationToken.None)).Should().BeEmpty();
  }

  [Fact]
  public async Task Validate_RecordsLastUsed_AndListNeverShowsTheToken()
  {
    var created = await _service.CreateAsync(7, "  Script  ", CancellationToken.None);
    await _service.ValidateAsync(created.Id, 7, CancellationToken.None);

    var listed = (await _service.ListAsync(7, CancellationToken.None)).Single();
    listed.Name.Should().Be("Script");
    listed.LastUsedAt.Should().NotBeNull();
    listed.GetType().GetProperty("Token").Should().BeNull();
  }

  [Fact]
  public async Task Create_RejectsAnEmptyName()
  {
    var act = () => _service.CreateAsync(7, "   ", CancellationToken.None);
    await act.Should().ThrowAsync<ArgumentException>();
  }

  private sealed class Factory : IDbContextFactory<BuoyAIDbContext>
  {
    private readonly DbContextOptions<BuoyAIDbContext> _options;
    public Factory(DbContextOptions<BuoyAIDbContext> options) => _options = options;
    public BuoyAIDbContext CreateDbContext() => new(_options);
  }
}
