using Xunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;

namespace BuoyAI.UnitTests.Repositories;

public class FamilyInvitationRepositoryTests
{
    private BuoyAIDbContext GetInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new BuoyAIDbContext(options);
    }

    private User CreateTestUser(int id)
    {
        return new User
        {
            Id = id,
            Username = $"user{id}",
            Email = $"user{id}@test.com",
            PasswordHash = "hash"
        };
    }

    private Family CreateTestFamily(int id, int createdByUserId)
    {
        return new Family
        {
            Id = id,
            Name = $"Family {id}",
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ReturnsInvitation_WithNavigationProperties()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family = CreateTestFamily(1, 1);
        var invitation = new FamilyInvitation
        {
            Id = 1,
            FamilyId = 1,
            InvitedEmail = "invited@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "token123",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Families.Add(family);
        context.FamilyInvitations.Add(invitation);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.InvitedEmail.Should().Be("invited@test.com");
        result.InvitedRole.Should().Be(FamilyMemberRole.Member);
        result.Family.Should().NotBeNull();
        result.InvitedByUser.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        // Act
        var result = await repository.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetByTokenAsync Tests

    [Fact]
    public async Task GetByTokenAsync_ReturnsInvitation_WhenValidAndNotExpired()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family = CreateTestFamily(1, 1);
        var invitation = new FamilyInvitation
        {
            Id = 1,
            FamilyId = 1,
            InvitedEmail = "invited@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "valid-token",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Families.Add(family);
        context.FamilyInvitations.Add(invitation);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByTokenAsync("valid-token");

        // Assert
        result.Should().NotBeNull();
        result!.InvitationToken.Should().Be("valid-token");
        result.IsAccepted.Should().BeFalse();
        result.Family.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByTokenAsync_ReturnsNull_WhenAlreadyAccepted()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family = CreateTestFamily(1, 1);
        var invitation = new FamilyInvitation
        {
            Id = 1,
            FamilyId = 1,
            InvitedEmail = "invited@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "accepted-token",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = true, // Already accepted
            AcceptedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Families.Add(family);
        context.FamilyInvitations.Add(invitation);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByTokenAsync("accepted-token");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByTokenAsync_ReturnsNull_WhenExpired()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family = CreateTestFamily(1, 1);
        var invitation = new FamilyInvitation
        {
            Id = 1,
            FamilyId = 1,
            InvitedEmail = "invited@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "expired-token",
            ExpiresAt = DateTime.UtcNow.AddDays(-1), // Expired
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow.AddDays(-8)
        };

        context.Users.Add(user);
        context.Families.Add(family);
        context.FamilyInvitations.Add(invitation);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByTokenAsync("expired-token");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetByFamilyIdAsync Tests

    [Fact]
    public async Task GetByFamilyIdAsync_ReturnsAllInvitations_IncludingExpired()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family = CreateTestFamily(1, 1);
        
        var invitation1 = new FamilyInvitation
        {
            Id = 1,
            FamilyId = 1,
            InvitedEmail = "user1@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "token1",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow
        };

        var invitation2 = new FamilyInvitation
        {
            Id = 2,
            FamilyId = 1,
            InvitedEmail = "user2@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.BudgetAdmin,
            InvitationToken = "token2",
            ExpiresAt = DateTime.UtcNow.AddDays(-1), // Expired
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow.AddDays(-8)
        };

        var invitation3 = new FamilyInvitation
        {
            Id = 3,
            FamilyId = 1,
            InvitedEmail = "user3@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "token3",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = true, // Accepted
            AcceptedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        };

        context.Users.Add(user);
        context.Families.Add(family);
        context.FamilyInvitations.AddRange(invitation1, invitation2, invitation3);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByFamilyIdAsync(1);

        // Assert
        result.Should().HaveCount(3); // Returns all invitations
        result.Should().Contain(i => i.InvitedEmail == "user1@test.com");
        result.Should().Contain(i => i.InvitedEmail == "user2@test.com");
        result.Should().Contain(i => i.InvitedEmail == "user3@test.com");
    }

    [Fact]
    public async Task GetByFamilyIdAsync_ReturnsEmptyList_WhenNoInvitations()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family = CreateTestFamily(1, 1);
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByFamilyIdAsync(1);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region GetPendingByFamilyIdAsync Tests

    [Fact]
    public async Task GetPendingByFamilyIdAsync_ReturnsOnlyPendingNotExpired()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family = CreateTestFamily(1, 1);
        
        var pendingValid = new FamilyInvitation
        {
            Id = 1,
            FamilyId = 1,
            InvitedEmail = "pending@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "pending-token",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow
        };

        var acceptedInvitation = new FamilyInvitation
        {
            Id = 2,
            FamilyId = 1,
            InvitedEmail = "accepted@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "accepted-token",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = true, // Accepted - should be excluded
            AcceptedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };

        var expiredInvitation = new FamilyInvitation
        {
            Id = 3,
            FamilyId = 1,
            InvitedEmail = "expired@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "expired-token",
            ExpiresAt = DateTime.UtcNow.AddDays(-1), // Expired - should be excluded
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow.AddDays(-8)
        };

        context.Users.Add(user);
        context.Families.Add(family);
        context.FamilyInvitations.AddRange(pendingValid, acceptedInvitation, expiredInvitation);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetPendingByFamilyIdAsync(1);

        // Assert
        result.Should().HaveCount(1);
        result.Should().Contain(i => i.InvitedEmail == "pending@test.com");
        result.Should().NotContain(i => i.IsAccepted);
        result.Should().NotContain(i => i.ExpiresAt < DateTime.UtcNow);
    }

    #endregion

    #region GetByEmailAsync Tests

    [Fact]
    public async Task GetByEmailAsync_ReturnsOnlyPendingNotExpired()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family1 = CreateTestFamily(1, 1);
        var family2 = CreateTestFamily(2, 1);

        var pendingValid1 = new FamilyInvitation
        {
            Id = 1,
            FamilyId = 1,
            InvitedEmail = "target@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "token1",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow
        };

        var pendingValid2 = new FamilyInvitation
        {
            Id = 2,
            FamilyId = 2,
            InvitedEmail = "target@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.BudgetAdmin,
            InvitationToken = "token2",
            ExpiresAt = DateTime.UtcNow.AddDays(3),
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow
        };

        var acceptedInvitation = new FamilyInvitation
        {
            Id = 3,
            FamilyId = 1,
            InvitedEmail = "target@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "accepted-token",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = true, // Should be excluded
            AcceptedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };

        var expiredInvitation = new FamilyInvitation
        {
            Id = 4,
            FamilyId = 1,
            InvitedEmail = "target@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "expired-token",
            ExpiresAt = DateTime.UtcNow.AddDays(-1), // Should be excluded
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow.AddDays(-8)
        };

        context.Users.Add(user);
        context.Families.AddRange(family1, family2);
        context.FamilyInvitations.AddRange(pendingValid1, pendingValid2, acceptedInvitation, expiredInvitation);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByEmailAsync("target@test.com");

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(i => i.FamilyId == 1 && i.InvitedRole == FamilyMemberRole.Member);
        result.Should().Contain(i => i.FamilyId == 2 && i.InvitedRole == FamilyMemberRole.BudgetAdmin);
        result.Should().NotContain(i => i.IsAccepted);
        result.All(i => i.Family != null).Should().BeTrue(); // Navigation loaded
    }

    [Fact]
    public async Task GetByEmailAsync_ReturnsEmptyList_WhenNoMatchingInvitations()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        // Act
        var result = await repository.GetByEmailAsync("nonexistent@test.com");

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsInvitation_AndSetsCreatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family = CreateTestFamily(1, 1);
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var invitation = new FamilyInvitation
        {
            FamilyId = 1,
            InvitedEmail = "new@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "new-token",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = false
        };

        // Act
        var result = await repository.CreateAsync(invitation);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().BeGreaterThan(0);
        result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        
        var saved = await context.FamilyInvitations.FindAsync(result.Id);
        saved.Should().NotBeNull();
        saved!.InvitedEmail.Should().Be("new@test.com");
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesInvitation()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family = CreateTestFamily(1, 1);
        var invitation = new FamilyInvitation
        {
            Id = 1,
            FamilyId = 1,
            InvitedEmail = "invited@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "token",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Families.Add(family);
        context.FamilyInvitations.Add(invitation);
        await context.SaveChangesAsync();

        // Modify invitation
        invitation.IsAccepted = true;
        invitation.AcceptedAt = DateTime.UtcNow;

        // Act
        var result = await repository.UpdateAsync(invitation);

        // Assert
        result.Should().NotBeNull();
        result.IsAccepted.Should().BeTrue();
        result.AcceptedAt.Should().NotBeNull();

        var updated = await context.FamilyInvitations.FindAsync(1);
        updated!.IsAccepted.Should().BeTrue();
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_RemovesInvitation_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family = CreateTestFamily(1, 1);
        var invitation = new FamilyInvitation
        {
            Id = 1,
            FamilyId = 1,
            InvitedEmail = "invited@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "token",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Families.Add(family);
        context.FamilyInvitations.Add(invitation);
        await context.SaveChangesAsync();

        // Act
        await repository.DeleteAsync(1);

        // Assert
        var deleted = await context.FamilyInvitations.FindAsync(1);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_DoesNothing_WhenInvitationNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        // Act & Assert
        await repository.DeleteAsync(999); // Should not throw
    }

    #endregion

    #region DeleteExpiredAsync Tests

    [Fact]
    public async Task DeleteExpiredAsync_RemovesOnlyExpiredNotAccepted()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family = CreateTestFamily(1, 1);

        var expiredNotAccepted1 = new FamilyInvitation
        {
            Id = 1,
            FamilyId = 1,
            InvitedEmail = "expired1@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "expired1",
            ExpiresAt = DateTime.UtcNow.AddDays(-1), // Should be deleted
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow.AddDays(-8)
        };

        var expiredNotAccepted2 = new FamilyInvitation
        {
            Id = 2,
            FamilyId = 1,
            InvitedEmail = "expired2@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "expired2",
            ExpiresAt = DateTime.UtcNow.AddDays(-5), // Should be deleted
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow.AddDays(-12)
        };

        var expiredButAccepted = new FamilyInvitation
        {
            Id = 3,
            FamilyId = 1,
            InvitedEmail = "accepted@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "accepted",
            ExpiresAt = DateTime.UtcNow.AddDays(-1), // Expired but accepted
            IsAccepted = true, // Should NOT be deleted
            AcceptedAt = DateTime.UtcNow.AddDays(-2),
            CreatedAt = DateTime.UtcNow.AddDays(-8)
        };

        var validNotExpired = new FamilyInvitation
        {
            Id = 4,
            FamilyId = 1,
            InvitedEmail = "valid@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "valid",
            ExpiresAt = DateTime.UtcNow.AddDays(7), // Not expired
            IsAccepted = false, // Should NOT be deleted
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Families.Add(family);
        context.FamilyInvitations.AddRange(expiredNotAccepted1, expiredNotAccepted2, expiredButAccepted, validNotExpired);
        await context.SaveChangesAsync();

        // Act
        await repository.DeleteExpiredAsync();

        // Assert
        var remaining = await context.FamilyInvitations.ToListAsync();
        remaining.Should().HaveCount(2);
        remaining.Should().Contain(i => i.Id == 3); // Accepted
        remaining.Should().Contain(i => i.Id == 4); // Not expired
        remaining.Should().NotContain(i => i.Id == 1); // Deleted
        remaining.Should().NotContain(i => i.Id == 2); // Deleted
    }

    [Fact]
    public async Task DeleteExpiredAsync_DoesNothing_WhenNoExpiredInvitations()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new FamilyInvitationRepository(context);

        var user = CreateTestUser(1);
        var family = CreateTestFamily(1, 1);
        var validInvitation = new FamilyInvitation
        {
            Id = 1,
            FamilyId = 1,
            InvitedEmail = "valid@test.com",
            InvitedByUserId = 1,
            InvitedRole = FamilyMemberRole.Member,
            InvitationToken = "valid",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsAccepted = false,
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Families.Add(family);
        context.FamilyInvitations.Add(validInvitation);
        await context.SaveChangesAsync();

        // Act
        await repository.DeleteExpiredAsync();

        // Assert
        var remaining = await context.FamilyInvitations.ToListAsync();
        remaining.Should().HaveCount(1);
        remaining.First().Id.Should().Be(1);
    }

    #endregion
}
