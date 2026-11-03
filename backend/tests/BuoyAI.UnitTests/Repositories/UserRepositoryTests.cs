using Xunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;

namespace BuoyAI.UnitTests.Repositories;

public class UserRepositoryTests
{
    private static BuoyAIDbContext GetInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new BuoyAIDbContext(options);
    }

    private static User CreateTestUser(int id, string username = "testuser", string email = "test@example.com")
    {
        return new User
        {
            Id = id,
            Username = username,
            Email = email,
            PasswordHash = "hashedpassword",
            FirstName = "Test",
            LastName = "User",
            Currency = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ReturnsUser_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser1", "test1@example.com");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var repository = new UserRepository(context);

        // Act
        var result = await repository.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.Username.Should().Be("testuser1");
        result.Email.Should().Be("test1@example.com");
    }

    [Fact]
    public async Task GetByIdAsync_IncludesNavigationProperties()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser1", "test1@example.com");
        
        var account = new Account
        {
            Id = 1,
            UserId = 1,
            PlaidAccountId = "acc123",
            Name = "Checking",
            Type = AccountType.Checking,
            Subtype = "checking"
        };
        
        var budget = new Budget
        {
            Id = 1,
            UserId = 1,
            Name = "Monthly Budget",
            Period = BudgetPeriod.Monthly,
            CurrentPeriodStart = DateTime.UtcNow,
            CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1)
        };
        
        context.Users.Add(user);
        context.Accounts.Add(account);
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        var repository = new UserRepository(context);

        // Act
        var result = await repository.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Accounts.Should().HaveCount(1);
        result.Accounts.First().Name.Should().Be("Checking");
        result.Budgets.Should().HaveCount(1);
        result.Budgets.First().Name.Should().Be("Monthly Budget");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserRepository(context);

        // Act
        var result = await repository.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetByUsernameAsync Tests

    [Fact]
    public async Task GetByUsernameAsync_ReturnsUser_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "uniqueuser", "unique@example.com");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var repository = new UserRepository(context);

        // Act
        var result = await repository.GetByUsernameAsync("uniqueuser");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.Username.Should().Be("uniqueuser");
    }

    [Fact]
    public async Task GetByUsernameAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserRepository(context);

        // Act
        var result = await repository.GetByUsernameAsync("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetByEmailAsync Tests

    [Fact]
    public async Task GetByEmailAsync_ReturnsUser_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser", "unique@example.com");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var repository = new UserRepository(context);

        // Act
        var result = await repository.GetByEmailAsync("unique@example.com");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.Email.Should().Be("unique@example.com");
    }

    [Fact]
    public async Task GetByEmailAsync_IsCaseInsensitive()
    {
        // A user stored lowercased must be found when looked up with different casing/whitespace
        // (e.g. the raw email from a Google ID token) — otherwise Google fails to link the account.
        using var context = GetInMemoryContext();
        context.Users.Add(CreateTestUser(1, "testuser", "user@example.com"));
        await context.SaveChangesAsync();

        var repository = new UserRepository(context);

        var result = await repository.GetByEmailAsync("  User@Example.COM ");

        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.Email.Should().Be("user@example.com");
    }

    [Fact]
    public async Task GetByEmailAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserRepository(context);

        // Act
        var result = await repository.GetByEmailAsync("nonexistent@example.com");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetByGoogleIdAsync Tests

    [Fact]
    public async Task GetByGoogleIdAsync_ReturnsUser_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "googleuser", "google@example.com");
        user.GoogleId = "google123";
        user.IsGoogleUser = true;
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var repository = new UserRepository(context);

        // Act
        var result = await repository.GetByGoogleIdAsync("google123");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.GoogleId.Should().Be("google123");
        result.IsGoogleUser.Should().BeTrue();
    }

    [Fact]
    public async Task GetByGoogleIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserRepository(context);

        // Act
        var result = await repository.GetByGoogleIdAsync("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsUser_AndSetsTimestamps()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserRepository(context);
        
        var user = new User
        {
            Username = "newuser",
            Email = "new@example.com",
            PasswordHash = "hashedpassword",
            FirstName = "New",
            LastName = "User",
            Currency = "USD"
        };

        // Act
        var result = await repository.CreateAsync(user);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().BeGreaterThan(0);
        result.Username.Should().Be("newuser");
        result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));

        // Verify persisted to database
        var savedUser = await context.Users.FindAsync(result.Id);
        savedUser.Should().NotBeNull();
        savedUser!.Username.Should().Be("newuser");
    }

    [Fact]
    public async Task CreateAsync_SetsDefaultValues()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserRepository(context);
        
        var user = new User
        {
            Username = "defaultuser",
            Email = "default@example.com",
            PasswordHash = "hashedpassword"
        };

        // Act
        var result = await repository.CreateAsync(user);

        // Assert
        result.Currency.Should().Be("USD");
        result.HasMfaEnabled.Should().BeFalse();
        result.MfaType.Should().Be(MfaType.None);
        result.IsDeleted.Should().BeFalse();
        result.IsGoogleUser.Should().BeFalse();
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesUser_AndSetsUpdatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "originaluser", "original@example.com");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        // Detach to avoid tracking conflicts
        context.Entry(user).State = EntityState.Detached;

        var repository = new UserRepository(context);
        
        // Wait to ensure UpdatedAt changes
        await Task.Delay(100);
        
        var updatedUser = await context.Users.FindAsync(1);
        updatedUser!.FirstName = "Updated";
        updatedUser.LastName = "Name";

        // Act
        var result = await repository.UpdateAsync(updatedUser);

        // Assert
        result.Should().NotBeNull();
        result.FirstName.Should().Be("Updated");
        result.LastName.Should().Be("Name");
        result.UpdatedAt.Should().BeAfter(user.CreatedAt);

        // Verify persisted to database
        var savedUser = await context.Users.FindAsync(1);
        savedUser.Should().NotBeNull();
        savedUser!.FirstName.Should().Be("Updated");
    }

    [Fact]
    public async Task UpdateAsync_UpdatesMfaFields()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "mfauser", "mfa@example.com");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        // Detach to avoid tracking conflicts
        context.Entry(user).State = EntityState.Detached;

        var repository = new UserRepository(context);
        
        var updatedUser = await context.Users.FindAsync(1);
        updatedUser!.HasMfaEnabled = true;
        updatedUser.MfaType = MfaType.Totp;
        updatedUser.TotpSecret = "encrypted_secret";
        updatedUser.MfaEnabledAt = DateTime.UtcNow;

        // Act
        var result = await repository.UpdateAsync(updatedUser);

        // Assert
        result.HasMfaEnabled.Should().BeTrue();
        result.MfaType.Should().Be(MfaType.Totp);
        result.TotpSecret.Should().Be("encrypted_secret");
        result.MfaEnabledAt.Should().NotBeNull();
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_RemovesUser_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "deleteuser", "delete@example.com");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var repository = new UserRepository(context);

        // Act
        await repository.DeleteAsync(1);

        // Assert
        var deletedUser = await context.Users.FindAsync(1);
        deletedUser.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow_WhenUserNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserRepository(context);

        // Act & Assert
        await repository.Invoking(r => r.DeleteAsync(999))
            .Should().NotThrowAsync();
    }

    #endregion

    #region CountGoogleRegistrationsSinceAsync Tests

    [Fact]
    public async Task CountGoogleRegistrationsSinceAsync_ReturnsCorrectCount()
    {
        // Arrange
        using var context = GetInMemoryContext();
        
        var oldGoogleUser = CreateTestUser(1, "oldgoogle", "old@google.com");
        oldGoogleUser.GoogleId = "google1";
        oldGoogleUser.IsGoogleUser = true;
        oldGoogleUser.CreatedAt = DateTime.UtcNow.AddDays(-10);
        
        var newGoogleUser1 = CreateTestUser(2, "newgoogle1", "new1@google.com");
        newGoogleUser1.GoogleId = "google2";
        newGoogleUser1.IsGoogleUser = true;
        newGoogleUser1.CreatedAt = DateTime.UtcNow.AddDays(-3);
        
        var newGoogleUser2 = CreateTestUser(3, "newgoogle2", "new2@google.com");
        newGoogleUser2.GoogleId = "google3";
        newGoogleUser2.IsGoogleUser = true;
        newGoogleUser2.CreatedAt = DateTime.UtcNow.AddDays(-1);
        
        var regularUser = CreateTestUser(4, "regular", "regular@example.com");
        regularUser.IsGoogleUser = false;
        regularUser.CreatedAt = DateTime.UtcNow.AddDays(-2);
        
        context.Users.AddRange(oldGoogleUser, newGoogleUser1, newGoogleUser2, regularUser);
        await context.SaveChangesAsync();

        var repository = new UserRepository(context);
        var sinceDate = DateTime.UtcNow.AddDays(-5);

        // Act
        var result = await repository.CountGoogleRegistrationsSinceAsync(sinceDate);

        // Assert
        result.Should().Be(2); // Only newGoogleUser1 and newGoogleUser2
    }

    [Fact]
    public async Task CountGoogleRegistrationsSinceAsync_ReturnsZero_WhenNoGoogleUsers()
    {
        // Arrange
        using var context = GetInMemoryContext();
        
        var regularUser = CreateTestUser(1, "regular", "regular@example.com");
        regularUser.IsGoogleUser = false;
        context.Users.Add(regularUser);
        await context.SaveChangesAsync();

        var repository = new UserRepository(context);
        var sinceDate = DateTime.UtcNow.AddDays(-7);

        // Act
        var result = await repository.CountGoogleRegistrationsSinceAsync(sinceDate);

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public async Task CountGoogleRegistrationsSinceAsync_ExcludesOldGoogleUsers()
    {
        // Arrange
        using var context = GetInMemoryContext();
        
        var oldGoogleUser = CreateTestUser(1, "oldgoogle", "old@google.com");
        oldGoogleUser.GoogleId = "google1";
        oldGoogleUser.IsGoogleUser = true;
        oldGoogleUser.CreatedAt = DateTime.UtcNow.AddDays(-30);
        
        context.Users.Add(oldGoogleUser);
        await context.SaveChangesAsync();

        var repository = new UserRepository(context);
        var sinceDate = DateTime.UtcNow.AddDays(-7);

        // Act
        var result = await repository.CountGoogleRegistrationsSinceAsync(sinceDate);

        // Assert
        result.Should().Be(0); // Old user excluded
    }

    #endregion
}
