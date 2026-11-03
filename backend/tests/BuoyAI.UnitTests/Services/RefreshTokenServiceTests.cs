using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Refresh-token rotation, reuse detection, expiry, and revocation. Uses an in-memory
/// DbContext; the raw token is only ever returned once, so tests capture it on issue.
/// </summary>
public class RefreshTokenServiceTests : IDisposable
{
  private readonly BuoyAIDbContext _context;
  private readonly RefreshTokenService _service;

  public RefreshTokenServiceTests()
  {
    // A single in-memory store shared by both the test's assertion context and every
    // context the factory hands the service — mirrors prod, where the service now runs
    // each operation on its own factory-created context rather than a shared one.
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;
    _context = new BuoyAIDbContext(options);

    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:RefreshTokenDays"] = "30" })
      .Build();
    _service = new RefreshTokenService(new SharedOptionsDbContextFactory(options), config, NullLogger<RefreshTokenService>.Instance);
  }

  /// <summary>Hands out fresh contexts over the same in-memory store the test asserts against.</summary>
  private sealed class SharedOptionsDbContextFactory : IDbContextFactory<BuoyAIDbContext>
  {
    private readonly DbContextOptions<BuoyAIDbContext> _options;
    public SharedOptionsDbContextFactory(DbContextOptions<BuoyAIDbContext> options) => _options = options;
    public BuoyAIDbContext CreateDbContext() => new(_options);
  }

  public void Dispose()
  {
    _context.Database.EnsureDeleted();
    _context.Dispose();
    GC.SuppressFinalize(this);
  }

  [Fact]
  public async Task IssueAsync_PersistsHashedToken_NotRawValue()
  {
    var issued = await _service.IssueAsync(userId: 7);

    issued.Token.Should().NotBeNullOrEmpty();
    var stored = await _context.RefreshTokens.SingleAsync();
    stored.UserId.Should().Be(7);
    stored.TokenHash.Should().NotBe(issued.Token); // stored as a hash, never the raw token
    stored.RevokedAt.Should().BeNull();
  }

  [Fact]
  public async Task IssueAsync_IssuesOnItsOwnFactoryContext_NotAnInjectedSharedContext()
  {
    // Regression for Sentry 7600799192: issuing a refresh token at the tail of the login
    // pipeline reused the request-scoped DbContext, so an operation still in flight on
    // that shared context threw "A second operation was started on this DbContext
    // instance…". The service now takes an IDbContextFactory and runs each call on its
    // own context. This test drives issuance purely through the factory-backed service
    // and asserts the row is committed and visible to the separate assertion context —
    // the service never receives, and cannot share, the request-scoped context.
    var first = await _service.IssueAsync(userId: 7);
    var second = await _service.IssueAsync(userId: 7);

    first.Token.Should().NotBe(second.Token);
    (await _context.RefreshTokens.CountAsync(t => t.UserId == 7)).Should().Be(2);
    (await _context.RefreshTokens.Where(t => t.UserId == 7).Select(t => t.Family).Distinct().CountAsync())
      .Should().Be(2); // each fresh login gets its own rotation family
  }

  [Fact]
  public async Task RotateAsync_ValidToken_RevokesOldAndIssuesNewInSameFamily()
  {
    var issued = await _service.IssueAsync(userId: 7);
    // AsNoTracking: the service now mutates rows on its own factory context, so the
    // assertion context must read fresh from the store rather than return a cached copy.
    var originalFamily = (await _context.RefreshTokens.AsNoTracking().SingleAsync()).Family;

    var rotated = await _service.RotateAsync(issued.Token);

    rotated.Should().NotBeNull();
    rotated!.UserId.Should().Be(7);
    rotated.Token.Should().NotBe(issued.Token);

    var rows = await _context.RefreshTokens.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
    rows.Should().HaveCount(2);
    rows[0].RevokedAt.Should().NotBeNull();               // old one revoked
    rows[1].RevokedAt.Should().BeNull();                  // replacement active
    rows[1].Family.Should().Be(originalFamily);           // same rotation family
  }

  [Fact]
  public async Task RotateAsync_UnknownToken_ReturnsNull()
  {
    var result = await _service.RotateAsync("not-a-real-token");

    result.Should().BeNull();
  }

  [Fact]
  public async Task RotateAsync_ReusedRotatedToken_AfterGraceWindow_RevokesEntireFamily()
  {
    var issued = await _service.IssueAsync(userId: 7);
    await _service.RotateAsync(issued.Token); // first use rotates + revokes the original

    // Push the rotation outside the reuse-grace window: this replay can't be a
    // multi-tab race anymore, so it's treated as theft.
    var original = await _context.RefreshTokens.OrderBy(t => t.Id).FirstAsync();
    original.RevokedAt = DateTime.UtcNow.AddMinutes(-5);
    await _context.SaveChangesAsync();

    var replay = await _service.RotateAsync(issued.Token);

    replay.Should().BeNull();
    var active = await _context.RefreshTokens.AsNoTracking().CountAsync(t => t.RevokedAt == null);
    active.Should().Be(0); // whole family revoked, including the previously-active replacement
  }

  [Fact]
  public async Task RotateAsync_ReusedRotatedToken_WithinGrace_IssuesSiblingInsteadOfRevoking()
  {
    // The multi-tab race (2026-07-18 prod incident, users 4+8): tabs share the refresh
    // cookie, both refresh at access-token expiry, the loser replays the just-rotated
    // token. Within the grace window that replay gets its own sibling token; nobody's
    // session is bricked.
    var issued = await _service.IssueAsync(userId: 7);
    var winner = await _service.RotateAsync(issued.Token);

    var loser = await _service.RotateAsync(issued.Token); // immediate replay = the race

    loser.Should().NotBeNull();
    loser!.Token.Should().NotBe(winner!.Token);
    var family = (await _context.RefreshTokens.AsNoTracking().OrderBy(t => t.Id).FirstAsync()).Family;
    var rows = await _context.RefreshTokens.AsNoTracking().ToListAsync();
    rows.Should().OnlyContain(t => t.Family == family); // sibling joins the same family
    rows.Count(t => t.RevokedAt == null).Should().Be(2); // both tabs hold a live leaf

    // And both leaves keep rotating independently afterward.
    (await _service.RotateAsync(winner.Token)).Should().NotBeNull();
    (await _service.RotateAsync(loser.Token)).Should().NotBeNull();
  }

  [Fact]
  public async Task RotateAsync_GraceReplay_DoesNotResurrectARevokedFamily()
  {
    var issued = await _service.IssueAsync(userId: 7);
    await _service.RotateAsync(issued.Token);
    await _service.RevokeAllForUserAsync(7); // family fully dead (theft response / logout-all)

    // Even a fresh-looking replay must not branch a dead family back to life.
    var replay = await _service.RotateAsync(issued.Token);

    replay.Should().BeNull();
    (await _context.RefreshTokens.AsNoTracking().CountAsync(t => t.RevokedAt == null)).Should().Be(0);
  }

  [Fact]
  public async Task RotateAsync_ExpiredToken_ReturnsNull()
  {
    var issued = await _service.IssueAsync(userId: 7);
    var row = await _context.RefreshTokens.SingleAsync();
    row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
    await _context.SaveChangesAsync();

    var result = await _service.RotateAsync(issued.Token);

    result.Should().BeNull();
  }

  [Fact]
  public async Task RevokeAsync_MarksTokenRevoked_SoRotateThenFails()
  {
    var issued = await _service.IssueAsync(userId: 7);

    await _service.RevokeAsync(issued.Token);

    (await _context.RefreshTokens.AsNoTracking().SingleAsync()).RevokedAt.Should().NotBeNull();
    // A revoked token presented to rotate is reuse → still null.
    (await _service.RotateAsync(issued.Token)).Should().BeNull();
  }

  [Fact]
  public async Task RevokeAllForUserAsync_RevokesEveryActiveSession_AndBreaksRotation()
  {
    // Three separate logins (families) for the same user, plus one already-revoked token.
    var s1 = await _service.IssueAsync(userId: 7);
    var s2 = await _service.IssueAsync(userId: 7);
    var s3 = await _service.IssueAsync(userId: 7);
    await _service.RevokeAsync(s3.Token); // already gone before the sweep

    var count = await _service.RevokeAllForUserAsync(7);

    count.Should().Be(2); // only the two still-active tokens counted
    (await _context.RefreshTokens.CountAsync(t => t.UserId == 7 && t.RevokedAt == null))
      .Should().Be(0);
    // Every device's next rotation now fails → forced re-auth.
    (await _service.RotateAsync(s1.Token)).Should().BeNull();
    (await _service.RotateAsync(s2.Token)).Should().BeNull();
  }

  [Fact]
  public async Task RevokeAllForUserAsync_LeavesOtherUsersSessionsIntact()
  {
    var mine = await _service.IssueAsync(userId: 7);
    var theirs = await _service.IssueAsync(userId: 99);

    await _service.RevokeAllForUserAsync(7);

    // The other user can still rotate normally.
    (await _service.RotateAsync(theirs.Token)).Should().NotBeNull();
    // Mine is dead.
    (await _service.RotateAsync(mine.Token)).Should().BeNull();
  }
}
