using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.Repositories;

public class UserConsentRepositoryTests
{
    private BuoyAIDbContext GetInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new BuoyAIDbContext(options);
    }

    private User CreateTestUser(int id = 1)
    {
        return new User
        {
            Id = id,
            Username = $"testuser{id}",
            Email = $"test{id}@example.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow
        };
    }

    #region GetByUserIdAsync Tests

    [Fact]
    public async Task GetByUserIdAsync_ReturnsUserConsents_OrderedByConsentedAtDescending()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserConsentRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var consent1 = new UserConsent
        {
            UserId = user.Id,
            ConsentType = "plaid_connection",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow.AddDays(-10),
            ConsentText = "I consent to Plaid connection",
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            UpdatedAt = DateTime.UtcNow.AddDays(-10)
        };

        var consent2 = new UserConsent
        {
            UserId = user.Id,
            ConsentType = "data_sharing",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow.AddDays(-5),
            ConsentText = "I consent to data sharing",
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            UpdatedAt = DateTime.UtcNow.AddDays(-5)
        };

        var consent3 = new UserConsent
        {
            UserId = user.Id,
            ConsentType = "marketing",
            ConsentVersion = "1.0",
            HasConsented = false,
            ConsentedAt = DateTime.UtcNow.AddDays(-2),
            ConsentText = "I do not consent to marketing",
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedAt = DateTime.UtcNow.AddDays(-2)
        };

        await context.UserConsents.AddRangeAsync(consent1, consent2, consent3);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAsync(user.Id);

        // Assert
        var resultList = result.ToList();
        resultList.Should().HaveCount(3);
        resultList[0].ConsentType.Should().Be("marketing"); // Most recent
        resultList[1].ConsentType.Should().Be("data_sharing");
        resultList[2].ConsentType.Should().Be("plaid_connection"); // Oldest
    }

    [Fact]
    public async Task GetByUserIdAsync_ReturnsEmptyList_WhenNoConsents()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserConsentRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(999);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByUserIdAsync_FiltersCorrectlyByUserId()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserConsentRepository(context);

        var user1 = CreateTestUser(1);
        var user2 = CreateTestUser(2);
        await context.Users.AddRangeAsync(user1, user2);

        var consent1 = new UserConsent
        {
            UserId = user1.Id,
            ConsentType = "plaid_connection",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var consent2 = new UserConsent
        {
            UserId = user2.Id,
            ConsentType = "plaid_connection",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await context.UserConsents.AddRangeAsync(consent1, consent2);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAsync(user1.Id);

        // Assert
        var resultList = result.ToList();
        resultList.Should().HaveCount(1);
        resultList[0].UserId.Should().Be(user1.Id);
    }

    #endregion

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ReturnsConsent_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserConsentRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var consent = new UserConsent
        {
            UserId = user.Id,
            ConsentType = "plaid_connection",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow,
            ConsentText = "I consent to Plaid connection",
            IpAddress = "192.168.1.1",
            UserAgent = "Mozilla/5.0",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await context.UserConsents.AddAsync(consent);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByIdAsync(consent.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(consent.Id);
        result.ConsentType.Should().Be("plaid_connection");
        result.ConsentVersion.Should().Be("1.0");
        result.HasConsented.Should().BeTrue();
        result.IpAddress.Should().Be("192.168.1.1");
        result.UserAgent.Should().Be("Mozilla/5.0");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserConsentRepository(context);

        // Act
        var result = await repository.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetLatestConsentAsync Tests

    [Fact]
    public async Task GetLatestConsentAsync_ReturnsLatestConsent_ForUserAndType()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserConsentRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var oldConsent = new UserConsent
        {
            UserId = user.Id,
            ConsentType = "plaid_connection",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow.AddDays(-30),
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            UpdatedAt = DateTime.UtcNow.AddDays(-30)
        };

        var latestConsent = new UserConsent
        {
            UserId = user.Id,
            ConsentType = "plaid_connection",
            ConsentVersion = "2.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var differentTypeConsent = new UserConsent
        {
            UserId = user.Id,
            ConsentType = "data_sharing",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow.AddHours(1), // Even more recent but different type
            CreatedAt = DateTime.UtcNow.AddHours(1),
            UpdatedAt = DateTime.UtcNow.AddHours(1)
        };

        await context.UserConsents.AddRangeAsync(oldConsent, latestConsent, differentTypeConsent);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetLatestConsentAsync(user.Id, "plaid_connection");

        // Assert
        result.Should().NotBeNull();
        result!.ConsentVersion.Should().Be("2.0");
        result.ConsentType.Should().Be("plaid_connection");
    }

    [Fact]
    public async Task GetLatestConsentAsync_ReturnsNull_WhenNoConsentForType()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserConsentRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var consent = new UserConsent
        {
            UserId = user.Id,
            ConsentType = "data_sharing",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await context.UserConsents.AddAsync(consent);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetLatestConsentAsync(user.Id, "plaid_connection");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestConsentAsync_FiltersCorrectlyByUser()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserConsentRepository(context);

        var user1 = CreateTestUser(1);
        var user2 = CreateTestUser(2);
        await context.Users.AddRangeAsync(user1, user2);

        var user1Consent = new UserConsent
        {
            UserId = user1.Id,
            ConsentType = "plaid_connection",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow.AddDays(-5),
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            UpdatedAt = DateTime.UtcNow.AddDays(-5)
        };

        var user2Consent = new UserConsent
        {
            UserId = user2.Id,
            ConsentType = "plaid_connection",
            ConsentVersion = "2.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow, // More recent but different user
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await context.UserConsents.AddRangeAsync(user1Consent, user2Consent);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetLatestConsentAsync(user1.Id, "plaid_connection");

        // Assert
        result.Should().NotBeNull();
        result!.UserId.Should().Be(user1.Id);
        result.ConsentVersion.Should().Be("1.0");
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsConsent_AndReturnsEntity()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserConsentRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);
        await context.SaveChangesAsync();

        var consent = new UserConsent
        {
            UserId = user.Id,
            ConsentType = "plaid_connection",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow,
            ConsentText = "I consent to Plaid connection",
            IpAddress = "192.168.1.1",
            UserAgent = "Mozilla/5.0",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Act
        var result = await repository.CreateAsync(consent);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().BeGreaterThan(0);
        result.UserId.Should().Be(user.Id);
        result.ConsentType.Should().Be("plaid_connection");

        // Verify it's in the database
        var savedConsent = await context.UserConsents.FindAsync(result.Id);
        savedConsent.Should().NotBeNull();
        savedConsent!.ConsentType.Should().Be("plaid_connection");
    }

    #endregion

    #region GetByConsentTypeAsync Tests

    [Fact]
    public async Task GetByConsentTypeAsync_ReturnsConsentsOfType_OrderedByConsentedAtDescending()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserConsentRepository(context);

        var user1 = CreateTestUser(1);
        var user2 = CreateTestUser(2);
        await context.Users.AddRangeAsync(user1, user2);

        var plaidConsent1 = new UserConsent
        {
            UserId = user1.Id,
            ConsentType = "plaid_connection",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow.AddDays(-10),
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            UpdatedAt = DateTime.UtcNow.AddDays(-10)
        };

        var plaidConsent2 = new UserConsent
        {
            UserId = user2.Id,
            ConsentType = "plaid_connection",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow.AddDays(-5),
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            UpdatedAt = DateTime.UtcNow.AddDays(-5)
        };

        var dataSharingConsent = new UserConsent
        {
            UserId = user1.Id,
            ConsentType = "data_sharing",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow, // Most recent but different type
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await context.UserConsents.AddRangeAsync(plaidConsent1, plaidConsent2, dataSharingConsent);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByConsentTypeAsync("plaid_connection");

        // Assert
        var resultList = result.ToList();
        resultList.Should().HaveCount(2);
        resultList[0].UserId.Should().Be(user2.Id); // More recent
        resultList[1].UserId.Should().Be(user1.Id); // Older
        resultList.All(c => c.ConsentType == "plaid_connection").Should().BeTrue();
    }

    [Fact]
    public async Task GetByConsentTypeAsync_ReturnsEmptyList_WhenNoConsentsOfType()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new UserConsentRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var consent = new UserConsent
        {
            UserId = user.Id,
            ConsentType = "data_sharing",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await context.UserConsents.AddAsync(consent);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByConsentTypeAsync("plaid_connection");

        // Assert
        result.Should().BeEmpty();
    }

    #endregion
}
