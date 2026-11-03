using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using BuoyAI.Core.Configuration;
using BuoyAI.Infrastructure.Services.CloudPortal;

namespace BuoyAI.UnitTests.Services;

public class CloudOwnerTokenIssuerTests
{
  internal const string Key = "a-test-owner-key-that-is-at-least-32-bytes-long";

  internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
  }

  private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

  private static CloudOwnerTokenIssuer Issuer(string key = Key, TimeProvider? time = null) =>
    new(Options.Create(new CloudPortalOptions { OwnerJwtKey = key }), time ?? new FixedTime(T0));

  /// <summary>The bridge's own validation (OwnerAuthentication.cs in the Cloud pairing library), copied: the app never references Cloud code.</summary>
  internal static TokenValidationParameters BridgeValidation(string key = Key, bool validateLifetime = true) => new()
  {
    ValidateIssuer = true,
    ValidIssuer = "buoy-cloud-portal",
    ValidateAudience = true,
    ValidAudience = "buoy-cloud",
    ValidateLifetime = validateLifetime,
    ValidateIssuerSigningKey = true,
    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
    NameClaimType = "sub",
    RoleClaimType = "role",
    ClockSkew = TimeSpan.FromSeconds(30)
  };

  private static JwtSecurityToken Read(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

  [Fact]
  public void Mint_SubIsTheCloudAccountId_AndIssuerAndAudienceMatchTheBridge()
  {
    var jwt = Read(Issuer().Mint("cus_ABC123", mfa: false));

    jwt.Subject.Should().Be("cus_ABC123");
    jwt.Issuer.Should().Be("buoy-cloud-portal");
    jwt.Audiences.Should().Equal("buoy-cloud");
    jwt.Header.Alg.Should().Be("HS256");
  }

  [Fact]
  public void Mint_WithoutStepUp_HasNoMfaClaim()
  {
    var jwt = Read(Issuer().Mint("cus_1", mfa: false));

    jwt.Claims.Should().NotContain(c => c.Type == "mfa");
  }

  [Fact]
  public void Mint_AfterStepUp_CarriesMfaTrue()
  {
    var jwt = Read(Issuer().Mint("cus_1", mfa: true));

    jwt.Claims.Single(c => c.Type == "mfa").Value.Should().Be("true");
  }

  [Fact]
  public void Mint_ExpiresFiveMinutesAfterIssue()
  {
    var jwt = Read(Issuer().Mint("cus_1", mfa: false));

    jwt.ValidFrom.Should().Be(T0.UtcDateTime);
    jwt.ValidTo.Should().Be(T0.UtcDateTime.AddMinutes(5));
  }

  [Fact]
  public void Mint_EachTokenHasItsOwnId()
  {
    var issuer = Issuer();
    Read(issuer.Mint("cus_1", false)).Id.Should().NotBe(Read(issuer.Mint("cus_1", false)).Id);
  }

  [Fact]
  public void Mint_PassesTheBridgesValidation_WithTheSharedKey()
  {
    var token = Issuer(time: TimeProvider.System).Mint("cus_1", mfa: true);

    var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }
      .ValidateToken(token, BridgeValidation(), out _);

    principal.FindFirst("sub")!.Value.Should().Be("cus_1");
    principal.FindFirst("mfa")!.Value.Should().Be("true");
  }

  [Fact]
  public void Mint_IsRefusedByTheBridge_WhenTheKeysDiffer()
  {
    var token = Issuer(time: TimeProvider.System).Mint("cus_1", mfa: true);

    var act = () => new JwtSecurityTokenHandler().ValidateToken(token, BridgeValidation("a-different-key-that-is-also-32-bytes-or-more"), out _);

    act.Should().Throw<SecurityTokenException>();
  }

  [Fact]
  public void Mint_IsRefusedByTheBridge_AfterItExpires()
  {
    var time = new FixedTime(DateTimeOffset.UtcNow.AddMinutes(-6));
    var token = Issuer(time: time).Mint("cus_1", mfa: true);

    var act = () => new JwtSecurityTokenHandler().ValidateToken(token, BridgeValidation(), out _);

    act.Should().Throw<SecurityTokenExpiredException>();
  }

  [Fact]
  public void Mint_WithoutAnOwnerId_Throws()
  {
    var act = () => Issuer().Mint("", mfa: true);

    act.Should().Throw<ArgumentException>();
  }

  [Fact]
  public void Mint_WithAShortKey_Throws()
  {
    var act = () => Issuer(key: "too-short").Mint("cus_1", mfa: false);

    act.Should().Throw<InvalidOperationException>();
  }

  [Fact]
  public void Options_AreConfiguredOnlyWithAKeyOfAtLeast32Bytes()
  {
    new CloudPortalOptions().IsConfigured.Should().BeFalse();
    new CloudPortalOptions { OwnerJwtKey = "short" }.IsConfigured.Should().BeFalse();
    new CloudPortalOptions { OwnerJwtKey = Key }.IsConfigured.Should().BeTrue();
    new CloudPortalOptions { OwnerJwtKey = Key, PairingUrl = "" }.IsConfigured.Should().BeFalse();
  }
}
