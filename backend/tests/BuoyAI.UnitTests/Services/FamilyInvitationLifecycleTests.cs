using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Family invitation flows run against the REAL family, member, invitation and user
/// repositories (EF in-memory), not mocks. The mocked FamilyServiceTests let the repository
/// return states the real queries never return (an accepted invitation by token), which is how
/// the "accepting twice is fine" fix shipped without ever running.
/// </summary>
public class FamilyInvitationLifecycleTests : IDisposable
{
  private const int OwnerId = 1;
  private const int InviteeId = 2;
  private const int FamilyId = 10;

  private readonly BuoyAIDbContext _context;
  private readonly FamilyService _service;

  public FamilyInvitationLifecycleTests()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString())
      .Options;
    _context = new BuoyAIDbContext(options);

    _context.Users.AddRange(
      new User
      {
        Id = OwnerId,
        Username = "owner",
        Email = "owner@example.com",
        FirstName = "Olive",
        LastName = "Owner",
        PasswordHash = "x",
        SubscriptionTier = SubscriptionTier.Premium,
        FamilyId = FamilyId
      },
      new User
      {
        Id = InviteeId,
        Username = "invitee",
        Email = "invitee@example.com",
        FirstName = "Ivy",
        LastName = "Invitee",
        PasswordHash = "x"
      });
    _context.Families.Add(new Family
    {
      Id = FamilyId,
      Name = "Owner Family",
      CreatedByUserId = OwnerId,
      CreatedAt = DateTime.UtcNow
    });
    _context.FamilyMembers.Add(new FamilyMember
    {
      FamilyId = FamilyId,
      UserId = OwnerId,
      Role = FamilyMemberRole.Owner,
      JoinedAt = DateTime.UtcNow,
      IsActive = true
    });
    _context.SaveChanges();

    var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

    _service = new FamilyService(
      new FamilyRepository(_context),
      new FamilyMemberRepository(_context),
      new FamilyInvitationRepository(_context),
      new UserRepository(_context),
      new Mock<IBudgetRepository>().Object,
      new Mock<ISavingsRepository>().Object,
      new Mock<IAccountRepository>().Object,
      new Mock<IPlaidConnectionRepository>().Object,
      new Mock<IEmailService>().Object,
      new Mock<INotificationService>().Object,
      configuration,
      TestEntitlements.For(configuration),
      new Mock<ILogger<FamilyService>>().Object,
      new BuoyAI.Infrastructure.Telemetry.BuoyAiMetrics(new TestMeterFactory()));
  }

  public void Dispose() => _context.Dispose();

  private Task<FamilyInvitationDto> InviteAsync(string email) =>
    _service.InviteMemberAsync(OwnerId, new InviteFamilyMemberRequest
    {
      Email = email,
      Role = FamilyMemberRole.Member
    });

  // Signing up from an invite link accepts the invitation, then opens the invitation page,
  // which calls this. It used to come back empty, so a brand new member saw
  // "This invitation link is invalid or has expired" right after joining.
  [Fact]
  public async Task GetInvitationByToken_AfterAccepting_StillFindsTheInvitationMarkedAccepted()
  {
    var invitation = await InviteAsync("invitee@example.com");
    await _service.AcceptInvitationAsync(InviteeId, invitation.InvitationToken);

    var reloaded = await _service.GetInvitationByTokenAsync(invitation.InvitationToken);

    reloaded.Should().NotBeNull();
    reloaded!.IsAccepted.Should().BeTrue();
  }

  // The double-submit guard added on 2026-08-19 only ever ran against a mock: the real
  // repository hid accepted invitations, so the second call said "Invitation not found".
  [Fact]
  public async Task AcceptInvitation_Twice_BySameUser_ReturnsTheMembershipInsteadOfFailing()
  {
    var invitation = await InviteAsync("invitee@example.com");
    await _service.AcceptInvitationAsync(InviteeId, invitation.InvitationToken);

    var second = await _service.AcceptInvitationAsync(InviteeId, invitation.InvitationToken);

    second.UserId.Should().Be(InviteeId);
    second.FamilyId.Should().Be(FamilyId);
    _context.FamilyMembers.Count(m => m.UserId == InviteeId).Should().Be(1);
  }

  [Fact]
  public async Task AcceptInvitation_ByAnotherUser_AfterItWasUsed_StillFails()
  {
    var invitation = await InviteAsync("invitee@example.com");
    await _service.AcceptInvitationAsync(InviteeId, invitation.InvitationToken);

    _context.Users.Add(new User { Id = 3, Username = "stranger", Email = "stranger@example.com", PasswordHash = "x" });
    await _context.SaveChangesAsync();

    var act = () => _service.AcceptInvitationAsync(3, invitation.InvitationToken);

    await act.Should().ThrowAsync<InvalidOperationException>();
  }

  // Leaving only marks the membership row inactive, and the database allows one row per
  // person per family (unique index IX_FamilyMembers_UserId_FamilyId). Accepting a new invite
  // inserted a second row, which Postgres rejects, so anyone who left could never come back.
  // (EF in-memory does not enforce the unique index, so this checks the row count instead.)
  [Fact]
  public async Task Rejoining_AfterLeaving_ReusesTheOldMembershipRow()
  {
    var first = await InviteAsync("invitee@example.com");
    await _service.AcceptInvitationAsync(InviteeId, first.InvitationToken);
    await _service.LeaveFamilyAsync(InviteeId);

    var second = await _service.InviteMemberAsync(OwnerId, new InviteFamilyMemberRequest
    {
      Email = "invitee@example.com",
      Role = FamilyMemberRole.ReadOnly
    });
    var rejoined = await _service.AcceptInvitationAsync(InviteeId, second.InvitationToken);

    rejoined.Role.Should().Be(FamilyMemberRole.ReadOnly);
    var rows = _context.FamilyMembers.Where(m => m.UserId == InviteeId && m.FamilyId == FamilyId).ToList();
    rows.Should().ContainSingle();
    rows[0].IsActive.Should().BeTrue();
    rows[0].Role.Should().Be(FamilyMemberRole.ReadOnly);
    (await _service.GetUserRoleInFamilyAsync(InviteeId)).Should().Be(FamilyMemberRole.ReadOnly);
  }

  // Accounts are stored lowercased, but the invite kept the address exactly as the owner typed
  // it and the email lookup compared case-sensitively, so the invitee's in-app invite list (and
  // the sign-in banner) never showed it.
  [Fact]
  public async Task PendingInvitationsForUser_FindsAnInviteTypedWithCapitals()
  {
    await InviteAsync("Invitee@Example.com");

    var mine = await _service.GetPendingInvitationsForUserAsync(InviteeId);

    mine.Should().ContainSingle();
  }

  [Fact]
  public async Task DuplicateInvite_WithDifferentCase_IsStillRejected()
  {
    await InviteAsync("Invitee@Example.com");

    var act = () => InviteAsync("invitee@example.com");

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*pending invitation*");
  }
}
