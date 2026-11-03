using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Repositories;

public class FamilyRepositoryTests
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
            Username = $"testuser{id}",
            Email = $"test{id}@example.com",
            PasswordHash = "test-hash"
        };
    }

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ReturnsFamily_WithMembers_WhenExists()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        var family = new Family
        {
            Name = "Test Family",
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var user1 = CreateTestUser(1);
        var user2 = CreateTestUser(2);
        user1.FamilyId = family.Id;
        user2.FamilyId = family.Id;
        context.Users.AddRange(user1, user2);

        var member1 = new FamilyMember
        {
            FamilyId = family.Id,
            UserId = user1.Id,
            Role = FamilyMemberRole.Owner,
            IsActive = true,
            JoinedAt = DateTime.UtcNow
        };
        var member2 = new FamilyMember
        {
            FamilyId = family.Id,
            UserId = user2.Id,
            Role = FamilyMemberRole.Member,
            IsActive = true,
            JoinedAt = DateTime.UtcNow
        };
        context.FamilyMembers.AddRange(member1, member2);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByIdAsync(family.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Family");
        result.Members.Should().HaveCount(2);
        result.Members.Should().Contain(m => m.Role == FamilyMemberRole.Owner);
        result.Members.Should().Contain(m => m.Role == FamilyMemberRole.Member);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        // Act
        var result = await repository.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetByUserIdAsync Tests

    [Fact]
    public async Task GetByUserIdAsync_ReturnsFamily_WhenUserHasFamilyId()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        var family = new Family
        {
            Name = "User's Family",
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var user = CreateTestUser(1);
        user.FamilyId = family.Id;
        context.Users.Add(user);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("User's Family");
        result.Id.Should().Be(family.Id);
    }

    [Fact]
    public async Task GetByUserIdAsync_ReturnsNull_WhenUserHasNoFamilyId()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        var user = CreateTestUser(1);
        user.FamilyId = null;
        context.Users.Add(user);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAsync(user.Id);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByUserIdAsync_ReturnsNull_WhenUserNotFound()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsFamily_AndSetsTimestamps()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        var beforeCreate = DateTime.UtcNow;
        var family = new Family
        {
            Name = "New Family",
            CreatedByUserId = 1
        };

        // Act
        var result = await repository.CreateAsync(family);
        var afterCreate = DateTime.UtcNow;

        // Assert
        result.Id.Should().BeGreaterThan(0);
        result.Name.Should().Be("New Family");
        result.CreatedAt.Should().BeOnOrAfter(beforeCreate).And.BeOnOrBefore(afterCreate);
        result.UpdatedAt.Should().BeOnOrAfter(beforeCreate).And.BeOnOrBefore(afterCreate);

        var savedFamily = await context.Families.FindAsync(result.Id);
        savedFamily.Should().NotBeNull();
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesFamily_AndSetsUpdatedAt()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        var family = new Family
        {
            Name = "Original Name",
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        };
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var originalUpdatedAt = family.UpdatedAt;
        await Task.Delay(10); // Ensure time difference

        // Act
        family.Name = "Updated Name";
        var result = await repository.UpdateAsync(family);

        // Assert
        result.Name.Should().Be("Updated Name");
        result.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_RemovesFamily_WhenExists()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        var family = new Family
        {
            Name = "Family to Delete",
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Families.Add(family);
        await context.SaveChangesAsync();

        // Act
        await repository.DeleteAsync(family.Id);

        // Assert
        var deletedFamily = await context.Families.FindAsync(family.Id);
        deletedFamily.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_DoesNothing_WhenFamilyNotFound()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        // Act
        await repository.DeleteAsync(999);

        // Assert - No exception thrown
        var families = await context.Families.ToListAsync();
        families.Should().BeEmpty();
    }

    #endregion

    #region GetMembersAsync Tests

    [Fact]
    public async Task GetMembersAsync_ReturnsActiveMembers_WithUsers()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        var family = new Family
        {
            Name = "Test Family",
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var user1 = CreateTestUser(1);
        var user2 = CreateTestUser(2);
        var user3 = CreateTestUser(3);
        context.Users.AddRange(user1, user2, user3);
        await context.SaveChangesAsync();

        var activeMember1 = new FamilyMember
        {
            FamilyId = family.Id,
            UserId = user1.Id,
            Role = FamilyMemberRole.Owner,
            IsActive = true,
            JoinedAt = DateTime.UtcNow
        };
        var activeMember2 = new FamilyMember
        {
            FamilyId = family.Id,
            UserId = user2.Id,
            Role = FamilyMemberRole.Member,
            IsActive = true,
            JoinedAt = DateTime.UtcNow
        };
        var inactiveMember = new FamilyMember
        {
            FamilyId = family.Id,
            UserId = user3.Id,
            Role = FamilyMemberRole.Member,
            IsActive = false, // Inactive - should be filtered out
            JoinedAt = DateTime.UtcNow
        };
        context.FamilyMembers.AddRange(activeMember1, activeMember2, inactiveMember);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetMembersAsync(family.Id);

        // Assert
        result.Should().HaveCount(2); // Only active members
        result.Should().Contain(m => m.UserId == user1.Id && m.Role == FamilyMemberRole.Owner);
        result.Should().Contain(m => m.UserId == user2.Id && m.Role == FamilyMemberRole.Member);
        result.Should().NotContain(m => m.UserId == user3.Id); // Inactive member excluded
    }

    [Fact]
    public async Task GetMembersAsync_ReturnsEmptyList_WhenNoActiveMembers()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        var family = new Family
        {
            Name = "Empty Family",
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Families.Add(family);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetMembersAsync(family.Id);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region GetMemberAsync Tests

    [Fact]
    public async Task GetMemberAsync_ReturnsMember_WhenExistsAndActive()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        var family = new Family
        {
            Name = "Test Family",
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var user = CreateTestUser(1);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            FamilyId = family.Id,
            UserId = user.Id,
            Role = FamilyMemberRole.Owner,
            IsActive = true,
            JoinedAt = DateTime.UtcNow
        };
        context.FamilyMembers.Add(member);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetMemberAsync(family.Id, user.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Role.Should().Be(FamilyMemberRole.Owner);
        result.UserId.Should().Be(user.Id);
        result.FamilyId.Should().Be(family.Id);
    }

    [Fact]
    public async Task GetMemberAsync_ReturnsNull_WhenMemberInactive()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        var family = new Family
        {
            Name = "Test Family",
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var user = CreateTestUser(1);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var inactiveMember = new FamilyMember
        {
            FamilyId = family.Id,
            UserId = user.Id,
            Role = FamilyMemberRole.Member,
            IsActive = false, // Inactive
            JoinedAt = DateTime.UtcNow
        };
        context.FamilyMembers.Add(inactiveMember);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetMemberAsync(family.Id, user.Id);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetMemberAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        // Act
        var result = await repository.GetMemberAsync(999, 999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region IsUserInFamilyAsync Tests

    [Fact]
    public async Task IsUserInFamilyAsync_ReturnsTrue_WhenUserIsActiveMember()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        var family = new Family
        {
            Name = "Test Family",
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var user = CreateTestUser(1);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            FamilyId = family.Id,
            UserId = user.Id,
            Role = FamilyMemberRole.Member,
            IsActive = true,
            JoinedAt = DateTime.UtcNow
        };
        context.FamilyMembers.Add(member);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.IsUserInFamilyAsync(user.Id, family.Id);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsUserInFamilyAsync_ReturnsFalse_WhenUserIsInactiveMember()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        var family = new Family
        {
            Name = "Test Family",
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var user = CreateTestUser(1);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var inactiveMember = new FamilyMember
        {
            FamilyId = family.Id,
            UserId = user.Id,
            Role = FamilyMemberRole.Member,
            IsActive = false, // Inactive
            JoinedAt = DateTime.UtcNow
        };
        context.FamilyMembers.Add(inactiveMember);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.IsUserInFamilyAsync(user.Id, family.Id);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserInFamilyAsync_ReturnsFalse_WhenUserNotInFamily()
    {
        // Arrange
        await using var context = GetInMemoryContext();
        var repository = new FamilyRepository(context);

        // Act
        var result = await repository.IsUserInFamilyAsync(999, 999);

        // Assert
        result.Should().BeFalse();
    }

    #endregion
}
