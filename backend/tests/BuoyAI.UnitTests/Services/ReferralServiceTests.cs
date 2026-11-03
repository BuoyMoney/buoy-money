using Xunit;
using Moq;
using FluentAssertions;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class ReferralServiceTests : IDisposable
{
  private readonly BuoyAIDbContext _context;
  private readonly Mock<IGamificationService> _gamification = new();
  private readonly ReferralService _service;

  private readonly IConfiguration _config;
  private readonly Mock<IBillingService> _billing = new();

  public ReferralServiceTests()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;
    _context = new BuoyAIDbContext(options);
    _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["App:BaseUrl"] = "https://staging.buoy-ai.com",
    }).Build();
    _service = new ReferralService(
      _context, _gamification.Object, _billing.Object, _config,
      new Mock<ILogger<ReferralService>>().Object);
  }

  public void Dispose() => _context.Dispose();

  private async Task<User> AddUserAsync(int id, string? code = null)
  {
    var u = new User { Id = id, Email = $"u{id}@x.com", ReferralCode = code };
    _context.Users.Add(u);
    await _context.SaveChangesAsync();
    return u;
  }

  [Fact]
  public async Task GetSummaryAsync_GeneratesCodeAndLink()
  {
    await AddUserAsync(1);
    var summary = await _service.GetSummaryAsync(1);
    summary.Code.Should().NotBeNullOrEmpty();
    summary.Link.Should().Contain(summary.Code);
    summary.SuccessfulReferrals.Should().Be(0);

    var user = await _context.Users.FirstAsync(u => u.Id == 1);
    user.ReferralCode.Should().Be(summary.Code); // persisted
  }

  [Fact]
  public async Task RedeemAsync_ValidCode_RecordsReferralAndAwardsConnector()
  {
    await AddUserAsync(1, "FRIEND22");
    await AddUserAsync(2);

    var ok = await _service.RedeemAsync(2, "friend22"); // case-insensitive

    ok.Should().BeTrue();
    (await _context.Set<Referral>().AnyAsync(r => r.ReferrerUserId == 1 && r.RefereeUserId == 2))
      .Should().BeTrue();
    _gamification.Verify(g => g.AwardAsync(1, It.IsAny<string>()), Times.Once);
  }

  [Fact]
  public async Task RedeemAsync_SelfReferral_IsRejected()
  {
    await AddUserAsync(1, "SELF2222");
    (await _service.RedeemAsync(1, "SELF2222")).Should().BeFalse();
  }

  [Fact]
  public async Task RedeemAsync_AlreadyReferred_IsRejected()
  {
    await AddUserAsync(1, "AAAA2222");
    await AddUserAsync(2);
    (await _service.RedeemAsync(2, "AAAA2222")).Should().BeTrue();
    (await _service.RedeemAsync(2, "AAAA2222")).Should().BeFalse(); // second time
  }

  [Fact]
  public async Task RedeemAsync_UnknownCode_IsRejected()
  {
    await AddUserAsync(2);
    (await _service.RedeemAsync(2, "NOPENOPE")).Should().BeFalse();
  }

  [Fact]
  public async Task GetSummaryAsync_LinkPointsAtTheGeneratingEnvironment()
  {
    // The link base was hardcoded to production, so every code minted on staging sent the
    // recipient to prod — the same bug class as the hardcoded email links.
    await AddUserAsync(1);

    var summary = await _service.GetSummaryAsync(1);

    summary.Link.Should().StartWith("https://staging.buoy-ai.com/app/?ref=");
  }

  [Fact]
  public async Task GetOrCreateCode_IsNotDerivableFromTheUserId()
  {
    // Codes used to be a pure function of the user id, and the algorithm ships in the repo,
    // so anyone could compute every user's code by iterating small integers. That destroys
    // the only signal a referral reward is paid on: that the code was actually shared.
    var codes = new HashSet<string>();
    for (var id = 1; id <= 25; id++)
    {
      await AddUserAsync(id);
      codes.Add((await _service.GetSummaryAsync(id)).Code);
    }

    // Distinct across users, and stable for a given user once assigned.
    codes.Should().HaveCount(25);
    var first = (await _service.GetSummaryAsync(1)).Code;
    (await _service.GetSummaryAsync(1)).Code.Should().Be(first);
  }

  [Fact]
  public async Task GeneratedCodes_AvoidVisuallyAmbiguousCharacters()
  {
    // Codes get read aloud and retyped off a screenshot, so 0/O and 1/I are excluded.
    await AddUserAsync(1);

    var code = (await _service.GetSummaryAsync(1)).Code;

    code.Should().HaveLength(8);
    code.Should().MatchRegex("^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{8}$");
  }
}
