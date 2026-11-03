using Microsoft.EntityFrameworkCore;
using FluentAssertions;
using Xunit;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;

namespace BuoyAI.UnitTests.Repositories;

public class FamilyMemberRepositoryTests
{
    private BuoyAIDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new BuoyAIDbContext(options);
    }

    private User CreateTestUser(string email = "test@example.com")
    {
        return new User
        {
            Email = email,
            Username = email.Split('@')[0],
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow
        };
    }

    private Family CreateTestFamily(string name = "Test Family")
    {
        return new Family
        {
            Name = name,
            CreatedAt = DateTime.UtcNow
        };
    }

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ReturnsMember_WhenExists()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user = CreateTestUser();
        var family = CreateTestFamily();
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            UserId = user.Id,
            FamilyId = family.Id,
            Role = FamilyMemberRole.Owner,
            JoinedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.FamilyMembers.Add(member);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByIdAsync(member.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(member.Id);
        result.Role.Should().Be(FamilyMemberRole.Owner);
    }

    [Fact]
    public async Task GetByIdAsync_IncludesUserAndFamily()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user = CreateTestUser("admin@example.com");
        var family = CreateTestFamily("Smith Family");
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            UserId = user.Id,
            FamilyId = family.Id,
            Role = FamilyMemberRole.Owner
        };
        context.FamilyMembers.Add(member);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByIdAsync(member.Id);

        // Assert
        result.Should().NotBeNull();
        result!.User.Should().NotBeNull();
        result.User.Email.Should().Be("admin@example.com");
        result.Family.Should().NotBeNull();
        result.Family.Name.Should().Be("Smith Family");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        // Act
        var result = await repository.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetByUserIdAsync Tests

    [Fact]
    public async Task GetByUserIdAsync_ReturnsMember_WhenExists()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user = CreateTestUser();
        var family = CreateTestFamily();
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            UserId = user.Id,
            FamilyId = family.Id,
            Role = FamilyMemberRole.Member,
            IsActive = true
        };
        context.FamilyMembers.Add(member);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result!.UserId.Should().Be(user.Id);
    }

    [Fact]
    public async Task GetByUserIdAsync_FiltersInactive()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user = CreateTestUser();
        var family = CreateTestFamily();
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            UserId = user.Id,
            FamilyId = family.Id,
            IsActive = false
        };
        context.FamilyMembers.Add(member);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAsync(user.Id);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByUserIdAsync_IncludesFamily()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user = CreateTestUser();
        var family = CreateTestFamily("Jones Family");
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            UserId = user.Id,
            FamilyId = family.Id,
            IsActive = true
        };
        context.FamilyMembers.Add(member);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Family.Should().NotBeNull();
        result.Family.Name.Should().Be("Jones Family");
    }

    #endregion

    #region GetByFamilyIdAsync Tests

    [Fact]
    public async Task GetByFamilyIdAsync_ReturnsAllActiveMembers()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user1 = CreateTestUser("user1@example.com");
        var user2 = CreateTestUser("user2@example.com");
        var family = CreateTestFamily();
        context.Users.AddRange(user1, user2);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var member1 = new FamilyMember
        {
            UserId = user1.Id,
            FamilyId = family.Id,
            Role = FamilyMemberRole.Owner,
            IsActive = true
        };
        var member2 = new FamilyMember
        {
            UserId = user2.Id,
            FamilyId = family.Id,
            Role = FamilyMemberRole.Member,
            IsActive = true
        };
        context.FamilyMembers.AddRange(member1, member2);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByFamilyIdAsync(family.Id);

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetByFamilyIdAsync_FiltersInactiveMembers()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user1 = CreateTestUser("active@example.com");
        var user2 = CreateTestUser("inactive@example.com");
        var family = CreateTestFamily();
        context.Users.AddRange(user1, user2);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var activeMember = new FamilyMember
        {
            UserId = user1.Id,
            FamilyId = family.Id,
            IsActive = true
        };
        var inactiveMember = new FamilyMember
        {
            UserId = user2.Id,
            FamilyId = family.Id,
            IsActive = false
        };
        context.FamilyMembers.AddRange(activeMember, inactiveMember);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByFamilyIdAsync(family.Id);

        // Assert
        result.Should().HaveCount(1);
        result.First().UserId.Should().Be(user1.Id);
    }

    [Fact]
    public async Task GetByFamilyIdAsync_IncludesUser()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user = CreateTestUser("member@example.com");
        var family = CreateTestFamily();
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            UserId = user.Id,
            FamilyId = family.Id,
            IsActive = true
        };
        context.FamilyMembers.Add(member);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByFamilyIdAsync(family.Id);

        // Assert
        result.Should().HaveCount(1);
        result.First().User.Should().NotBeNull();
        result.First().User.Email.Should().Be("member@example.com");
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsMember_AndSetsJoinedAt()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user = CreateTestUser();
        var family = CreateTestFamily();
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            UserId = user.Id,
            FamilyId = family.Id,
            Role = FamilyMemberRole.Owner
        };

        var beforeCreate = DateTime.UtcNow;

        // Act
        var result = await repository.CreateAsync(member);

        // Assert
        result.Id.Should().BeGreaterThan(0);
        result.JoinedAt.Should().BeOnOrAfter(beforeCreate);
        result.JoinedAt.Should().BeOnOrBefore(DateTime.UtcNow);
    }

    [Fact]
    public async Task CreateAsync_SetsDefaultValues()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user = CreateTestUser();
        var family = CreateTestFamily();
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            UserId = user.Id,
            FamilyId = family.Id
        };

        // Act
        var result = await repository.CreateAsync(member);

        // Assert
        result.Role.Should().Be(FamilyMemberRole.Member);
        result.IsActive.Should().BeTrue();
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesMember()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user = CreateTestUser();
        var family = CreateTestFamily();
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            UserId = user.Id,
            FamilyId = family.Id,
            Role = FamilyMemberRole.Member
        };
        context.FamilyMembers.Add(member);
        await context.SaveChangesAsync();

        // Detach to simulate fresh update
        context.Entry(member).State = EntityState.Detached;

        // Act
        member.Role = FamilyMemberRole.BudgetAdmin;
        var result = await repository.UpdateAsync(member);

        // Assert
        var updated = await context.FamilyMembers.FindAsync(member.Id);
        updated!.Role.Should().Be(FamilyMemberRole.BudgetAdmin);
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_SoftDeletesMember()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user = CreateTestUser();
        var family = CreateTestFamily();
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            UserId = user.Id,
            FamilyId = family.Id,
            IsActive = true
        };
        context.FamilyMembers.Add(member);
        await context.SaveChangesAsync();

        // Act
        await repository.DeleteAsync(member.Id);

        // Assert
        var deleted = await context.FamilyMembers.FindAsync(member.Id);
        deleted.Should().NotBeNull();
        deleted!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow_WhenMemberNotFound()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        // Act & Assert
        await repository.Invoking(r => r.DeleteAsync(999))
            .Should().NotThrowAsync();
    }

    #endregion

    #region RemoveMemberAsync Tests

    [Fact]
    public async Task RemoveMemberAsync_SoftDeletesMember()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        var user = CreateTestUser();
        var family = CreateTestFamily();
        context.Users.Add(user);
        context.Families.Add(family);
        await context.SaveChangesAsync();

        var member = new FamilyMember
        {
            UserId = user.Id,
            FamilyId = family.Id,
            IsActive = true
        };
        context.FamilyMembers.Add(member);
        await context.SaveChangesAsync();

        // Act
        await repository.RemoveMemberAsync(family.Id, user.Id);

        // Assert
        var removed = await context.FamilyMembers
            .FirstOrDefaultAsync(m => m.FamilyId == family.Id && m.UserId == user.Id);
        removed.Should().NotBeNull();
        removed!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveMemberAsync_DoesNotThrow_WhenMemberNotFound()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new FamilyMemberRepository(context);

        // Act & Assert
        await repository.Invoking(r => r.RemoveMemberAsync(999, 999))
            .Should().NotThrowAsync();
    }

    #endregion
}
