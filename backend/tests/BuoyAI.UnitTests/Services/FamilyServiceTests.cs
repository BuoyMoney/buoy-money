using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class FamilyServiceTests
{
  private readonly Mock<IFamilyRepository> _familyRepository = new();
  private readonly Mock<IFamilyMemberRepository> _familyMemberRepository = new();
  private readonly Mock<IFamilyInvitationRepository> _familyInvitationRepository = new();
  private readonly Mock<IUserRepository> _userRepository = new();
  private readonly Mock<IBudgetRepository> _budgetRepository = new();
  private readonly Mock<ISavingsRepository> _savingsRepository = new();
  private readonly Mock<IAccountRepository> _accountRepository = new();
  private readonly Mock<IPlaidConnectionRepository> _plaidConnectionRepository = new();
  private readonly Mock<IEmailService> _emailService = new();
  private readonly Mock<INotificationService> _notificationService = new();
  private readonly Mock<IConfiguration> _configuration = new();
  private readonly Mock<ILogger<FamilyService>> _logger = new();

  private FamilyService CreateService()
  {
    return new FamilyService(
      _familyRepository.Object,
      _familyMemberRepository.Object,
      _familyInvitationRepository.Object,
      _userRepository.Object,
      _budgetRepository.Object,
      _savingsRepository.Object,
      _accountRepository.Object,
      _plaidConnectionRepository.Object,
      _emailService.Object,
      _notificationService.Object,
      _configuration.Object,
      TestEntitlements.For(_configuration.Object),
      _logger.Object,
      new BuoyAI.Infrastructure.Telemetry.BuoyAiMetrics(new TestMeterFactory()));
  }

  [Fact]
  public async Task CreateFamilyAsync_WithStandardSubscription_ShouldThrowUnauthorized()
  {
    // Arrange
    var userId = 7;
    var request = new CreateFamilyRequest { Name = "Smith Family" };
    var standardUser = new User
    {
      Id = userId,
      Email = "standard@example.com",
      Username = "standard-user",
      SubscriptionTier = SubscriptionTier.Standard
    };

    _userRepository
      .Setup(repo => repo.GetByIdAsync(userId))
      .ReturnsAsync(standardUser);

    var service = CreateService();

    // Act
    var act = async () => await service.CreateFamilyAsync(userId, request);

    // Assert
    await act.Should().ThrowAsync<SubscriptionRequiredException>()
      .WithMessage("*Premium subscription*");

    _familyRepository.Verify(repo => repo.CreateAsync(It.IsAny<Family>()), Times.Never);
    _familyMemberRepository.Verify(repo => repo.CreateAsync(It.IsAny<FamilyMember>()), Times.Never);
  }

  [Fact]
  public async Task CreateFamilyAsync_WithPremiumSubscription_ShouldCreateFamily()
  {
    // Arrange
    var userId = 11;
    var request = new CreateFamilyRequest { Name = "Premium Fam" };
    var premiumUser = new User
    {
      Id = userId,
      Email = "premium@example.com",
      Username = "premium-user",
      SubscriptionTier = SubscriptionTier.Premium
    };

    var familyId = 501;

    _userRepository
      .Setup(repo => repo.GetByIdAsync(userId))
      .ReturnsAsync(premiumUser);

    _familyRepository
      .Setup(repo => repo.CreateAsync(It.IsAny<Family>()))
      .Callback<Family>(family =>
      {
        family.Id = familyId;
      })
      .ReturnsAsync((Family family) => family);

    _familyMemberRepository
      .Setup(repo => repo.CreateAsync(It.IsAny<FamilyMember>()))
      .ReturnsAsync((FamilyMember member) => member);

    _familyMemberRepository
      .Setup(repo => repo.GetByFamilyIdAsync(familyId))
      .ReturnsAsync(new List<FamilyMember>
      {
        new()
        {
          Id = 900,
          FamilyId = familyId,
          UserId = userId,
          Role = FamilyMemberRole.Owner,
          JoinedAt = DateTime.UtcNow,
          IsActive = true
        }
      });

    _userRepository
      .Setup(repo => repo.UpdateAsync(It.IsAny<User>()))
      .Callback<User>(updated => premiumUser.FamilyId = updated.FamilyId)
      .ReturnsAsync((User user) => user);

    _budgetRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(new List<Budget>());
    _savingsRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(new List<SavingsGoal>());
    _accountRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(new List<Account>());
    _plaidConnectionRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(new List<PlaidConnection>());

    var service = CreateService();

    // Act
    var result = await service.CreateFamilyAsync(userId, request);

    // Assert
    result.Should().NotBeNull();
    result.Id.Should().Be(familyId);
    result.CreatedByUserId.Should().Be(userId);
    result.SubscriptionTier.Should().Be(SubscriptionTier.Premium);

    _familyRepository.Verify(repo => repo.CreateAsync(It.IsAny<Family>()), Times.Once);
    _familyMemberRepository.Verify(repo => repo.CreateAsync(It.IsAny<FamilyMember>()), Times.Once);
    _budgetRepository.Verify(repo => repo.GetByUserIdAsync(userId), Times.Once);
  }

  [Fact]
  public async Task CreateFamilyAsync_WithPendingInvitations_ShouldAutoDeclineAndNotifyInviters()
  {
    // Arrange — a premium user who has two pending invitations creates their own
    // family; both invitations should be deleted and both inviters notified.
    var userId = 11;
    var request = new CreateFamilyRequest { Name = "Premium Fam" };
    var premiumUser = new User
    {
      Id = userId,
      Email = "premium@example.com",
      Username = "premium-user",
      FirstName = "Pat",
      LastName = "Premium",
      SubscriptionTier = SubscriptionTier.Premium
    };

    var familyId = 501;

    _userRepository
      .Setup(repo => repo.GetByIdAsync(userId))
      .ReturnsAsync(premiumUser);

    _familyRepository
      .Setup(repo => repo.CreateAsync(It.IsAny<Family>()))
      .Callback<Family>(family => family.Id = familyId)
      .ReturnsAsync((Family family) => family);

    _familyMemberRepository
      .Setup(repo => repo.CreateAsync(It.IsAny<FamilyMember>()))
      .ReturnsAsync((FamilyMember member) => member);

    _familyMemberRepository
      .Setup(repo => repo.GetByFamilyIdAsync(familyId))
      .ReturnsAsync(new List<FamilyMember>
      {
        new() { Id = 900, FamilyId = familyId, UserId = userId, Role = FamilyMemberRole.Owner, JoinedAt = DateTime.UtcNow, IsActive = true }
      });

    _userRepository
      .Setup(repo => repo.UpdateAsync(It.IsAny<User>()))
      .Callback<User>(updated => premiumUser.FamilyId = updated.FamilyId)
      .ReturnsAsync((User user) => user);

    _budgetRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(new List<Budget>());
    _savingsRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(new List<SavingsGoal>());
    _accountRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(new List<Account>());
    _plaidConnectionRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(new List<PlaidConnection>());

    var pendingInvitations = new List<FamilyInvitation>
    {
      new() { Id = 71, FamilyId = 800, InvitedEmail = premiumUser.Email, InvitedByUserId = 42 },
      new() { Id = 72, FamilyId = 801, InvitedEmail = premiumUser.Email, InvitedByUserId = 43 }
    };
    _familyInvitationRepository
      .Setup(repo => repo.GetByEmailAsync(premiumUser.Email))
      .ReturnsAsync(pendingInvitations);

    var service = CreateService();

    // Act
    var result = await service.CreateFamilyAsync(userId, request);

    // Assert
    result.Should().NotBeNull();
    result.Id.Should().Be(familyId);

    _familyInvitationRepository.Verify(repo => repo.DeleteAsync(71), Times.Once);
    _familyInvitationRepository.Verify(repo => repo.DeleteAsync(72), Times.Once);
    _notificationService.Verify(svc => svc.CreateNotificationAsync(42,
      It.Is<CreateNotificationRequest>(r => r.Type == "family_invitation_declined")), Times.Once);
    _notificationService.Verify(svc => svc.CreateNotificationAsync(43,
      It.Is<CreateNotificationRequest>(r => r.Type == "family_invitation_declined")), Times.Once);
  }

  [Fact]
  public async Task CreateFamilyAsync_WhenNotificationFails_ShouldStillCreateFamily()
  {
    // Arrange — a failing inviter notification must not abort family creation.
    var userId = 11;
    var request = new CreateFamilyRequest { Name = "Premium Fam" };
    var premiumUser = new User
    {
      Id = userId,
      Email = "premium@example.com",
      Username = "premium-user",
      FirstName = "Pat",
      LastName = "Premium",
      SubscriptionTier = SubscriptionTier.Premium
    };

    var familyId = 501;

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(premiumUser);
    _familyRepository
      .Setup(repo => repo.CreateAsync(It.IsAny<Family>()))
      .Callback<Family>(family => family.Id = familyId)
      .ReturnsAsync((Family family) => family);
    _familyMemberRepository.Setup(repo => repo.CreateAsync(It.IsAny<FamilyMember>())).ReturnsAsync((FamilyMember member) => member);
    _familyMemberRepository
      .Setup(repo => repo.GetByFamilyIdAsync(familyId))
      .ReturnsAsync(new List<FamilyMember>
      {
        new() { Id = 900, FamilyId = familyId, UserId = userId, Role = FamilyMemberRole.Owner, JoinedAt = DateTime.UtcNow, IsActive = true }
      });
    _userRepository
      .Setup(repo => repo.UpdateAsync(It.IsAny<User>()))
      .Callback<User>(updated => premiumUser.FamilyId = updated.FamilyId)
      .ReturnsAsync((User user) => user);
    _budgetRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(new List<Budget>());
    _savingsRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(new List<SavingsGoal>());
    _accountRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(new List<Account>());
    _plaidConnectionRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(new List<PlaidConnection>());

    _familyInvitationRepository
      .Setup(repo => repo.GetByEmailAsync(premiumUser.Email))
      .ReturnsAsync(new List<FamilyInvitation>
      {
        new() { Id = 71, FamilyId = 800, InvitedEmail = premiumUser.Email, InvitedByUserId = 42 }
      });
    _notificationService
      .Setup(svc => svc.CreateNotificationAsync(It.IsAny<int>(), It.IsAny<CreateNotificationRequest>()))
      .ThrowsAsync(new InvalidOperationException("notification backend down"));

    var service = CreateService();

    // Act
    var result = await service.CreateFamilyAsync(userId, request);

    // Assert — family still created, invitation still deleted despite the failure
    result.Should().NotBeNull();
    result.Id.Should().Be(familyId);
    _familyInvitationRepository.Verify(repo => repo.DeleteAsync(71), Times.Once);
  }

  [Fact]
  public async Task CreateFamilyAsync_WhenUserNotFound_ShouldThrowUnauthorized()
  {
    // Arrange
    var userId = 99;
    var request = new CreateFamilyRequest { Name = "Test Family" };
    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync((User?)null);

    var service = CreateService();

    // Act
    var act = async () => await service.CreateFamilyAsync(userId, request);

    // Assert
    // EnsurePremiumSubscriptionAsync is called first and will throw InvalidOperationException
    // because the user is not found
    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("*User not found*");
  }

  [Fact]
  public async Task CreateFamilyAsync_WhenUserAlreadyInFamily_ShouldThrowInvalidOperation()
  {
    // Arrange
    var userId = 100;
    var request = new CreateFamilyRequest { Name = "Test Family" };
    var user = new User
    {
      Id = userId,
      Email = "test@example.com",
      Username = "test-user",
      SubscriptionTier = SubscriptionTier.Premium,
      FamilyId = 50 // Already in a family
    };
    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);

    var service = CreateService();

    // Act
    var act = async () => await service.CreateFamilyAsync(userId, request);

    // Assert
    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("*already in a family*");
  }

  [Fact]
  public async Task GetFamilyAsync_WhenUserNotInFamily_ShouldReturnNull()
  {
    // Arrange
    var userId = 101;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = null };
    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);

    var service = CreateService();

    // Act
    var result = await service.GetFamilyAsync(userId);

    // Assert
    result.Should().BeNull();
  }

  [Fact]
  public async Task GetFamilyAsync_WhenUserInFamily_ShouldReturnFamilyDto()
  {
    // Arrange
    var userId = 102;
    var familyId = 60;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId, SubscriptionTier = SubscriptionTier.Premium };
    var family = new Family
    {
      Id = familyId,
      Name = "Test Family",
      CreatedByUserId = userId
    };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyRepository.Setup(repo => repo.GetByIdAsync(familyId)).ReturnsAsync(family);
    _familyMemberRepository.Setup(repo => repo.GetByFamilyIdAsync(familyId))
      .ReturnsAsync(new List<FamilyMember>
      {
        new() { Id = 1, FamilyId = familyId, UserId = userId, Role = FamilyMemberRole.Owner }
      });

    var service = CreateService();

    // Act
    var result = await service.GetFamilyAsync(userId);

    // Assert
    result.Should().NotBeNull();
    result!.Id.Should().Be(familyId);
    result.Name.Should().Be("Test Family");
  }

  [Fact]
  public async Task UpdateFamilyAsync_WhenNotOwner_ShouldThrowUnauthorized()
  {
    // Arrange
    var userId = 103;
    var familyId = 70;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId };
    var request = new UpdateFamilyRequest { Name = "New Name" };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        Id = 1,
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.BudgetAdmin // Not owner
      });

    var service = CreateService();

    // Act
    var act = async () => await service.UpdateFamilyAsync(userId, request);

    // Assert
    await act.Should().ThrowAsync<UnauthorizedAccessException>()
      .WithMessage("*Only family owners*");
  }

  [Fact]
  public async Task UpdateFamilyAsync_AsOwner_ShouldUpdateFamily()
  {
    // Arrange
    var userId = 104;
    var familyId = 80;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId, SubscriptionTier = SubscriptionTier.Premium };
    var family = new Family
    {
      Id = familyId,
      Name = "Old Name",
      CreatedByUserId = userId
    };
    var request = new UpdateFamilyRequest { Name = "New Name" };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        Id = 1,
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Owner
      });
    _familyRepository.Setup(repo => repo.GetByIdAsync(familyId)).ReturnsAsync(family);
    _familyRepository.Setup(repo => repo.UpdateAsync(It.IsAny<Family>())).ReturnsAsync(family);
    _familyMemberRepository.Setup(repo => repo.GetByFamilyIdAsync(familyId))
      .ReturnsAsync(new List<FamilyMember>
      {
        new() { Id = 1, FamilyId = familyId, UserId = userId, Role = FamilyMemberRole.Owner }
      });

    var service = CreateService();

    // Act
    var result = await service.UpdateFamilyAsync(userId, request);

    // Assert
    result.Should().NotBeNull();
    family.Name.Should().Be("New Name");
    _familyRepository.Verify(repo => repo.UpdateAsync(It.IsAny<Family>()), Times.Once);
  }

  [Fact]
  public async Task DeleteFamilyAsync_WhenNotOwner_ShouldThrowUnauthorized()
  {
    // Arrange
    var userId = 105;
    var familyId = 90;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.BudgetAdmin // Not owner
      });

    var service = CreateService();

    // Act
    var act = async () => await service.DeleteFamilyAsync(userId);

    // Assert
    // CanUserManageFamilyAsync is called first and throws the first exception
    await act.Should().ThrowAsync<UnauthorizedAccessException>()
      .WithMessage("*Only family owners can delete*");
  }

  [Fact]
  public async Task DeleteFamilyAsync_AsOwner_ShouldDeleteFamily()
  {
    // Arrange
    var userId = 106;
    var familyId = 95;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Owner
      });
    _familyRepository.Setup(repo => repo.DeleteAsync(familyId)).Returns(Task.CompletedTask);

    var service = CreateService();

    // Act
    await service.DeleteFamilyAsync(userId);

    // Assert
    _familyRepository.Verify(repo => repo.DeleteAsync(familyId), Times.Once);
  }

  [Fact]
  public async Task DeleteFamilyAsync_AsOwner_ResetsMembersInheritedPremium()
  {
    // Arrange
    var ownerId = 110;
    var familyId = 96;
    var owner = new User { Id = ownerId, Email = "owner@example.com", FamilyId = familyId, SubscriptionTier = SubscriptionTier.Premium };
    var inheritor = new User { Id = 111, Email = "kid@example.com", FamilyId = familyId, SubscriptionTier = SubscriptionTier.PremiumInherited };
    var ownPlan = new User { Id = 112, Email = "partner@example.com", FamilyId = familyId, SubscriptionTier = SubscriptionTier.Standard };

    _userRepository.Setup(repo => repo.GetByIdAsync(ownerId)).ReturnsAsync(owner);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(ownerId))
      .ReturnsAsync(new FamilyMember { FamilyId = familyId, UserId = ownerId, Role = FamilyMemberRole.Owner });
    _familyRepository.Setup(repo => repo.GetMembersAsync(familyId)).ReturnsAsync(new List<FamilyMember>
    {
      new() { FamilyId = familyId, UserId = ownerId, User = owner, Role = FamilyMemberRole.Owner },
      new() { FamilyId = familyId, UserId = inheritor.Id, User = inheritor, Role = FamilyMemberRole.Member },
      new() { FamilyId = familyId, UserId = ownPlan.Id, User = ownPlan, Role = FamilyMemberRole.ReadOnly },
    });

    var service = CreateService();

    // Act
    await service.DeleteFamilyAsync(ownerId);

    // Assert
    inheritor.SubscriptionTier.Should().BeNull();
    owner.SubscriptionTier.Should().Be(SubscriptionTier.Premium);
    ownPlan.SubscriptionTier.Should().Be(SubscriptionTier.Standard);
    _userRepository.Verify(repo => repo.UpdateAsync(inheritor), Times.Once);
    _userRepository.Verify(repo => repo.UpdateAsync(owner), Times.Never);
    _familyRepository.Verify(repo => repo.DeleteAsync(familyId), Times.Once);
  }

  [Fact]
  public async Task LeaveFamilyAsync_WhenNotInFamily_ShouldThrowInvalidOperation()
  {
    // Arrange
    var userId = 107;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = null };
    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);

    var service = CreateService();

    // Act
    var act = async () => await service.LeaveFamilyAsync(userId);

    // Assert
    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("*not in a family*");
  }

  [Fact]
  public async Task LeaveFamilyAsync_AsOwner_ShouldThrowInvalidOperation()
  {
    // Arrange
    var userId = 108;
    var familyId = 100;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Owner
      });

    var service = CreateService();

    // Act
    var act = async () => await service.LeaveFamilyAsync(userId);

    // Assert
    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("*owner can't leave*");
  }

  [Fact]
  public async Task LeaveFamilyAsync_AsMember_ShouldRemoveMemberAndUpdateUser()
  {
    // Arrange
    var userId = 109;
    var familyId = 110;
    var user = new User
    {
      Id = userId,
      Email = "member@example.com",
      FirstName = "John",
      LastName = "Doe",
      FamilyId = familyId
    };
    var family = new Family
    {
      Id = familyId,
      Name = "Test Family",
      CreatedByUserId = 1
    };
    var owner = new User
    {
      Id = 1,
      Email = "owner@example.com",
      FirstName = "Owner"
    };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _userRepository.Setup(repo => repo.GetByIdAsync(1)).ReturnsAsync(owner);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Member
      });
    _familyRepository.Setup(repo => repo.GetByIdAsync(familyId)).ReturnsAsync(family);
    _familyMemberRepository.Setup(repo => repo.RemoveMemberAsync(familyId, userId))
      .Returns(Task.CompletedTask);
    _userRepository.Setup(repo => repo.UpdateAsync(It.IsAny<User>()))
      .ReturnsAsync((User u) => u);
    _emailService.Setup(svc => svc.SendFamilyMemberLeftAsync(
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
      .Returns(Task.CompletedTask);

    var service = CreateService();

    // Act
    await service.LeaveFamilyAsync(userId);

    // Assert
    _familyMemberRepository.Verify(repo => repo.RemoveMemberAsync(familyId, userId), Times.Once);
    _userRepository.Verify(repo => repo.UpdateAsync(It.Is<User>(u => u.FamilyId == null)), Times.Once);
    _emailService.Verify(svc => svc.SendFamilyMemberLeftAsync(
        owner.Email, owner.FirstName, "John Doe", user.Email), Times.Once);
  }

  [Fact]
  public async Task GetFamilyMembersAsync_WhenNotInFamily_ShouldThrowInvalidOperation()
  {
    // Arrange
    var userId = 111;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = null };
    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);

    var service = CreateService();

    // Act
    var act = async () => await service.GetFamilyMembersAsync(userId);

    // Assert
    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("*not in a family*");
  }

  [Fact]
  public async Task GetFamilyMembersAsync_WhenInFamily_ShouldReturnMembers()
  {
    // Arrange
    var userId = 112;
    var familyId = 120;
    var user = new User
    {
      Id = userId,
      Email = "owner@example.com",
      FirstName = "Owner",
      LastName = "User",
      FamilyId = familyId
    };
    var member2 = new User
    {
      Id = 113,
      Email = "member@example.com",
      FirstName = "Member",
      LastName = "User"
    };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _userRepository.Setup(repo => repo.GetByIdAsync(113)).ReturnsAsync(member2);
    _familyMemberRepository.Setup(repo => repo.GetByFamilyIdAsync(familyId))
      .ReturnsAsync(new List<FamilyMember>
      {
        new()
        {
          Id = 1,
          FamilyId = familyId,
          UserId = userId,
          Role = FamilyMemberRole.Owner,
          JoinedAt = DateTime.UtcNow
        },
        new()
        {
          Id = 2,
          FamilyId = familyId,
          UserId = 113,
          Role = FamilyMemberRole.Member,
          JoinedAt = DateTime.UtcNow
        }
      });

    var service = CreateService();

    // Act
    var result = await service.GetFamilyMembersAsync(userId);

    // Assert
    result.Should().HaveCount(2);
    result.Should().Contain(m => m.UserId == userId && m.Role == FamilyMemberRole.Owner);
    result.Should().Contain(m => m.UserId == 113 && m.Role == FamilyMemberRole.Member);
  }

  [Fact]
  public async Task UpdateMemberRoleAsync_WhenNotOwner_ShouldThrowUnauthorized()
  {
    // Arrange
    var userId = 114;
    var targetUserId = 115;
    var familyId = 130;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.BudgetAdmin // Not owner
      });

    var service = CreateService();

    // Act
    var act = async () => await service.UpdateMemberRoleAsync(userId, targetUserId, FamilyMemberRole.BudgetAdmin);

    // Assert
    await act.Should().ThrowAsync<UnauthorizedAccessException>()
      .WithMessage("*Only family owners*");
  }

  [Fact]
  public async Task UpdateMemberRoleAsync_TargetIsOwner_ShouldThrowInvalidOperation()
  {
    // Arrange
    var userId = 116;
    var targetUserId = 117;
    var familyId = 140;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Owner
      });
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(targetUserId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = targetUserId,
        Role = FamilyMemberRole.Owner // Target is owner
      });

    var service = CreateService();

    // Act
    var act = async () => await service.UpdateMemberRoleAsync(userId, targetUserId, FamilyMemberRole.BudgetAdmin);

    // Assert
    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("*Cannot change the owner's role*");
  }

  [Fact]
  public async Task UpdateMemberRoleAsync_NewRoleIsOwner_ShouldThrowInvalidOperation()
  {
    // Arrange - a role update must never mint a second Owner (defence-in-depth
    // against a malformed payload binding NewRole to 0/Owner).
    var userId = 216;
    var targetUserId = 217;
    var familyId = 240;
    var user = new User { Id = userId, Email = "owner@example.com", FamilyId = familyId };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Owner
      });
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(targetUserId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = targetUserId,
        Role = FamilyMemberRole.Member
      });

    var service = CreateService();

    // Act
    var act = async () => await service.UpdateMemberRoleAsync(userId, targetUserId, FamilyMemberRole.Owner);

    // Assert
    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("*Cannot promote a member to Owner*");
    _familyMemberRepository.Verify(repo => repo.UpdateAsync(It.IsAny<FamilyMember>()), Times.Never);
  }

  [Fact]
  public async Task UpdateMemberRoleAsync_ValidRequest_ShouldUpdateRoleAndSendEmail()
  {
    // Arrange
    var userId = 118;
    var targetUserId = 119;
    var familyId = 150;
    var user = new User
    {
      Id = userId,
      Email = "owner@example.com",
      FirstName = "Owner",
      LastName = "User",
      FamilyId = familyId
    };
    var targetUser = new User
    {
      Id = targetUserId,
      Email = "member@example.com",
      FirstName = "Target",
      LastName = "User"
    };
    var targetMember = new FamilyMember
    {
      FamilyId = familyId,
      UserId = targetUserId,
      Role = FamilyMemberRole.Member
    };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _userRepository.Setup(repo => repo.GetByIdAsync(targetUserId)).ReturnsAsync(targetUser);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Owner
      });
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(targetUserId))
      .ReturnsAsync(targetMember);
    _familyMemberRepository.Setup(repo => repo.UpdateAsync(It.IsAny<FamilyMember>()))
      .ReturnsAsync(targetMember);
    _emailService.Setup(svc => svc.SendFamilyRoleChangedAsync(
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
      .Returns(Task.CompletedTask);

    var service = CreateService();

    // Act
    var result = await service.UpdateMemberRoleAsync(userId, targetUserId, FamilyMemberRole.BudgetAdmin);

    // Assert
    result.Should().NotBeNull();
    result.Role.Should().Be(FamilyMemberRole.BudgetAdmin);
    targetMember.Role.Should().Be(FamilyMemberRole.BudgetAdmin);
    _familyMemberRepository.Verify(repo => repo.UpdateAsync(It.IsAny<FamilyMember>()), Times.Once);
    _emailService.Verify(svc => svc.SendFamilyRoleChangedAsync(
        targetUser.Email, targetUser.FirstName, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
        Times.Once);
  }

  [Fact]
  public async Task RemoveMemberAsync_WhenNotOwner_ShouldThrowUnauthorized()
  {
    // Arrange
    var userId = 120;
    var targetUserId = 121;
    var familyId = 160;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Member // Not owner
      });

    var service = CreateService();

    // Act
    var act = async () => await service.RemoveMemberAsync(userId, targetUserId);

    // Assert
    await act.Should().ThrowAsync<UnauthorizedAccessException>()
      .WithMessage("*Only family owners*");
  }

  [Fact]
  public async Task RemoveMemberAsync_TargetIsOwner_ShouldThrowInvalidOperation()
  {
    // Arrange
    var userId = 122;
    var targetUserId = 123;
    var familyId = 170;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Owner
      });
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(targetUserId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = targetUserId,
        Role = FamilyMemberRole.Owner
      });

    var service = CreateService();

    // Act
    var act = async () => await service.RemoveMemberAsync(userId, targetUserId);

    // Assert
    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("*Cannot remove the family owner*");
  }

  [Fact]
  public async Task RemoveMemberAsync_ValidRequest_ShouldRemoveMemberAndSendEmail()
  {
    // Arrange
    var userId = 124;
    var targetUserId = 125;
    var familyId = 180;
    var user = new User
    {
      Id = userId,
      Email = "owner@example.com",
      FirstName = "Owner",
      LastName = "User",
      FamilyId = familyId
    };
    var targetUser = new User
    {
      Id = targetUserId,
      Email = "member@example.com",
      FirstName = "Target",
      LastName = "User"
    };
    var family = new Family
    {
      Id = familyId,
      Name = "Test Family",
      CreatedByUserId = userId
    };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _userRepository.Setup(repo => repo.GetByIdAsync(targetUserId)).ReturnsAsync(targetUser);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Owner
      });
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(targetUserId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = targetUserId,
        Role = FamilyMemberRole.Member
      });
    _familyRepository.Setup(repo => repo.GetByIdAsync(familyId)).ReturnsAsync(family);
    _familyMemberRepository.Setup(repo => repo.RemoveMemberAsync(familyId, targetUserId))
      .Returns(Task.CompletedTask);
    _userRepository.Setup(repo => repo.UpdateAsync(It.IsAny<User>()))
      .ReturnsAsync((User u) => u);
    _emailService.Setup(svc => svc.SendFamilyMemberRemovedAsync(
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
      .Returns(Task.CompletedTask);

    var service = CreateService();

    // Act
    await service.RemoveMemberAsync(userId, targetUserId);

    // Assert
    _familyMemberRepository.Verify(repo => repo.RemoveMemberAsync(familyId, targetUserId), Times.Once);
    _userRepository.Verify(repo => repo.UpdateAsync(It.Is<User>(u => u.Id == targetUserId && u.FamilyId == null)), Times.Once);
    _emailService.Verify(svc => svc.SendFamilyMemberRemovedAsync(
        targetUser.Email, targetUser.FirstName, family.Name, "Owner User"), Times.Once);
  }

  [Fact]
  public async Task CanUserModifyFinancialDataAsync_WhenNotInFamily_ShouldReturnTrue()
  {
    // Arrange
    var userId = 126;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = null };
    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);

    var service = CreateService();

    // Act
    var result = await service.CanUserModifyFinancialDataAsync(userId);

    // Assert
    result.Should().BeTrue();
  }

  [Fact]
  public async Task CanUserModifyFinancialDataAsync_AsOwner_ShouldReturnTrue()
  {
    // Arrange
    var userId = 127;
    var familyId = 190;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Owner
      });

    var service = CreateService();

    // Act
    var result = await service.CanUserModifyFinancialDataAsync(userId);

    // Assert
    result.Should().BeTrue();
  }

  [Fact]
  public async Task CanUserModifyFinancialDataAsync_AsBudgetAdmin_ShouldReturnTrue()
  {
    // Arrange
    var userId = 128;
    var familyId = 200;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.BudgetAdmin
      });

    var service = CreateService();

    // Act
    var result = await service.CanUserModifyFinancialDataAsync(userId);

    // Assert
    result.Should().BeTrue();
  }

  [Fact]
  public async Task CanUserModifyFinancialDataAsync_AsMember_ShouldReturnTrue()
  {
    // Arrange
    var userId = 129;
    var familyId = 210;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Member
      });

    var service = CreateService();

    // Act
    var result = await service.CanUserModifyFinancialDataAsync(userId);

    // Assert
    result.Should().BeTrue(); // Member CAN modify financial data
  }

  [Fact]
  public async Task CanUserModifyFinancialDataAsync_AsReadOnly_ShouldReturnFalse()
  {
    // Arrange
    var userId = 132;
    var familyId = 240;
    var user = new User { Id = userId, Email = "test@example.com", FamilyId = familyId };

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.ReadOnly
      });

    var service = CreateService();

    // Act
    var result = await service.CanUserModifyFinancialDataAsync(userId);

    // Assert
    result.Should().BeFalse(); // ReadOnly cannot modify financial data
  }

  [Fact]
  public async Task CanUserManageFamilyAsync_AsOwner_ShouldReturnTrue()
  {
    // Arrange
    var userId = 130;
    var familyId = 220;

    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.Owner
      });

    var service = CreateService();

    // Act
    var result = await service.CanUserManageFamilyAsync(userId);

    // Assert
    result.Should().BeTrue();
  }

  [Fact]
  public async Task CanUserManageFamilyAsync_AsNonOwner_ShouldReturnFalse()
  {
    // Arrange
    var userId = 131;
    var familyId = 230;

    _familyMemberRepository.Setup(repo => repo.GetByUserIdAsync(userId))
      .ReturnsAsync(new FamilyMember
      {
        FamilyId = familyId,
        UserId = userId,
        Role = FamilyMemberRole.BudgetAdmin
      });

    var service = CreateService();

    // Act
    var result = await service.CanUserManageFamilyAsync(userId);

    // Assert
    result.Should().BeFalse();
  }

  // Regression: a double-submit on the accept screen (observed in prod 2026-08-20, the same
  // token POSTed twice 3s apart) threw "Invitation has already been accepted" on the second
  // call, showing the user an error toast for a join that had fully succeeded.
  [Fact]
  public async Task AcceptInvitationAsync_WhenAlreadyAcceptedBySameUser_ShouldReturnExistingMembership()
  {
    // Arrange
    const int userId = 2;
    const int familyId = 2;
    const string token = "already-consumed-token";

    _familyInvitationRepository.Setup(r => r.GetByTokenAnyStateAsync(token)).ReturnsAsync(new FamilyInvitation
    {
      Id = 3,
      FamilyId = familyId,
      InvitedEmail = "member@example.com",
      InvitationToken = token,
      IsAccepted = true,
      AcceptedAt = DateTime.UtcNow.AddSeconds(-3),
      ExpiresAt = DateTime.UtcNow.AddDays(7),
      InvitedRole = FamilyMemberRole.BudgetAdmin
    });
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(new FamilyMember
    {
      Id = 5,
      FamilyId = familyId,
      UserId = userId,
      Role = FamilyMemberRole.BudgetAdmin,
      IsActive = true,
      JoinedAt = DateTime.UtcNow.AddSeconds(-3)
    });
    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(new User
    {
      Id = userId,
      Email = "member@example.com",
      Username = "member",
      FirstName = "Mem",
      LastName = "Ber",
      FamilyId = familyId
    });

    // Act
    var result = await CreateService().AcceptInvitationAsync(userId, token);

    // Assert — the replay returns the membership rather than throwing, and creates nothing new.
    result.Should().NotBeNull();
    result.UserId.Should().Be(userId);
    result.FamilyId.Should().Be(familyId);
    result.Role.Should().Be(FamilyMemberRole.BudgetAdmin);
    _familyMemberRepository.Verify(r => r.CreateAsync(It.IsAny<FamilyMember>()), Times.Never);
    _familyInvitationRepository.Verify(r => r.UpdateAsync(It.IsAny<FamilyInvitation>()), Times.Never);
  }

  [Fact]
  public async Task AcceptInvitationAsync_WhenAlreadyAcceptedByDifferentUser_ShouldStillThrow()
  {
    // Arrange — the replay guard must not hand someone else's consumed invitation to a caller
    // who is not already a member of that family.
    const int callerId = 9;
    const int familyId = 2;
    const string token = "someone-elses-token";

    _familyInvitationRepository.Setup(r => r.GetByTokenAnyStateAsync(token)).ReturnsAsync(new FamilyInvitation
    {
      Id = 3,
      FamilyId = familyId,
      InvitedEmail = "member@example.com",
      InvitationToken = token,
      IsAccepted = true,
      AcceptedAt = DateTime.UtcNow.AddMinutes(-5),
      ExpiresAt = DateTime.UtcNow.AddDays(7),
      InvitedRole = FamilyMemberRole.BudgetAdmin
    });
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(callerId)).ReturnsAsync((FamilyMember?)null);

    // Act
    var act = async () => await CreateService().AcceptInvitationAsync(callerId, token);

    // Assert
    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("Invitation has already been accepted");
  }

  // The sweep's "does the owner have premium" test must agree with the GRANT side in
  // AcceptInvitationAsync (AdminPolicy.HasPremiumAccess). It used to judge on the passed-in
  // tier alone, which missed the allowlist and active-trial arms, so a still-covered owner's
  // members could be stripped.
  private void SetupOwnedFamily(int ownerId, int memberId, User owner, User member)
  {
    _userRepository.Setup(r => r.GetByIdAsync(ownerId)).ReturnsAsync(owner);
    _userRepository.Setup(r => r.GetByIdAsync(memberId)).ReturnsAsync(member);
    _familyRepository.Setup(r => r.GetByIdAsync(owner.FamilyId!.Value))
      .ReturnsAsync(new Family { Id = owner.FamilyId.Value, Name = "Test Family", CreatedByUserId = ownerId });
    _familyMemberRepository.Setup(r => r.GetByFamilyIdAsync(owner.FamilyId.Value)).ReturnsAsync(new[]
    {
      new FamilyMember { Id = 1, FamilyId = owner.FamilyId.Value, UserId = ownerId, Role = FamilyMemberRole.Owner, IsActive = true },
      new FamilyMember { Id = 2, FamilyId = owner.FamilyId.Value, UserId = memberId, Role = FamilyMemberRole.BudgetAdmin, IsActive = true }
    });
  }

  [Fact]
  public async Task UpdateFamilyMembersTierAsync_WhenOwnerIsOnActiveTrial_ShouldKeepInheritedPremium()
  {
    // Arrange — no Trial tier exists, so a trialing owner sits at Standard with IsTrialActive.
    const int ownerId = 4, memberId = 2;
    var owner = new User
    {
      Id = ownerId, Email = "owner@example.com", Username = "owner", FamilyId = 2,
      SubscriptionTier = SubscriptionTier.Standard,
      IsTrialActive = true, TrialEndDate = DateTime.UtcNow.AddDays(10)
    };
    var member = new User
    {
      Id = memberId, Email = "member@example.com", Username = "member", FamilyId = 2,
      SubscriptionTier = SubscriptionTier.PremiumInherited
    };
    SetupOwnedFamily(ownerId, memberId, owner, member);

    // Act
    await CreateService().UpdateFamilyMembersTierAsync(ownerId, SubscriptionTier.Standard);

    // Assert — coverage is still live, so the member keeps it.
    member.SubscriptionTier.Should().Be(SubscriptionTier.PremiumInherited);
  }

  [Fact]
  public async Task UpdateFamilyMembersTierAsync_WhenTrialHasLapsed_ShouldStillStripInheritedPremium()
  {
    // Arrange — every lapse path clears IsTrialActive and persists it BEFORE calling the sweep.
    const int ownerId = 4, memberId = 2;
    var owner = new User
    {
      Id = ownerId, Email = "owner@example.com", Username = "owner", FamilyId = 2,
      SubscriptionTier = null, IsTrialActive = false, TrialEndDate = DateTime.UtcNow.AddDays(-1)
    };
    var member = new User
    {
      Id = memberId, Email = "member@example.com", Username = "member", FamilyId = 2,
      SubscriptionTier = SubscriptionTier.PremiumInherited
    };
    SetupOwnedFamily(ownerId, memberId, owner, member);

    // Act
    await CreateService().UpdateFamilyMembersTierAsync(ownerId, null);

    // Assert — the deliberate strip contract is preserved.
    member.SubscriptionTier.Should().BeNull();
  }
}
