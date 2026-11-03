using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using Xunit;
using FluentAssertions;

namespace BuoyAI.UnitTests.Repositories;

public class SubscriptionRepositoryTests
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

    private Account CreateTestAccount(int id, int userId)
    {
        return new Account
        {
            Id = id,
            UserId = userId,
            PlaidAccountId = $"plaid_account_{id}",
            Name = $"Test Account {id}",
            Type = AccountType.Checking,
            CurrentBalance = 1000.00m,
            AvailableBalance = 1000.00m,
            Currency = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private Transaction CreateTestTransaction(int id, int accountId, decimal amount, string merchantName)
    {
        return new Transaction
        {
            Id = id,
            AccountId = accountId,
            PlaidTransactionId = $"plaid_txn_{id}",
            Amount = amount,
            Description = $"Payment to {merchantName}",
            MerchantName = merchantName,
            Date = DateTime.UtcNow,
            CategoryId = (int)TransactionCategory.Subscriptions,
            Type = TransactionType.Debit,
            CreatedAt = DateTime.UtcNow
        };
    }

    #region GetByUserIdAsync Tests

    [Fact]
    public async Task GetByUserIdAsync_ReturnsUserSubscriptions_OrderedByNextDueDate()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var subscriptions = new[]
        {
            new Subscription
            {
                UserId = user.Id,
                Name = "Netflix",
                Amount = 15.99m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = DateTime.UtcNow.AddDays(10),
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Streaming,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Subscription
            {
                UserId = user.Id,
                Name = "Spotify",
                Amount = 9.99m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = DateTime.UtcNow.AddDays(5), // Earlier due date
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Streaming,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Subscription
            {
                UserId = user.Id,
                Name = "Adobe Creative Cloud",
                Amount = 54.99m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = DateTime.UtcNow.AddDays(15),
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Software,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        await context.Subscriptions.AddRangeAsync(subscriptions);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAsync(user.Id);

        // Assert
        var resultList = result.ToList();
        resultList.Should().HaveCount(3);
        resultList[0].Name.Should().Be("Spotify"); // Earliest due date
        resultList[1].Name.Should().Be("Netflix");
        resultList[2].Name.Should().Be("Adobe Creative Cloud");
        resultList.Should().AllSatisfy(s =>
        {
            s.User.Should().NotBeNull();
            s.UserId.Should().Be(user.Id);
        });
    }

    [Fact]
    public async Task GetByUserIdAsync_ReturnsEmptyList_WhenNoSubscriptions()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(999);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByUserIdAsync_IncludesLinkedTransaction()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var account = CreateTestAccount(1, user.Id);
        await context.Accounts.AddAsync(account);

        var transaction = CreateTestTransaction(1, account.Id, 15.99m, "Netflix");
        await context.Transactions.AddAsync(transaction);

        var subscription = new Subscription
        {
            UserId = user.Id,
            Name = "Netflix",
            Amount = 15.99m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = DateTime.UtcNow.AddDays(30),
            Status = SubscriptionStatus.Active,
            Category = SubscriptionCategory.Streaming,
            LinkedTransactionId = transaction.Id,
            IsDetected = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await context.Subscriptions.AddAsync(subscription);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAsync(user.Id);

        // Assert
        var resultList = result.ToList();
        resultList.Should().HaveCount(1);
        resultList[0].LinkedTransaction.Should().NotBeNull();
        resultList[0].LinkedTransaction!.Id.Should().Be(transaction.Id);
        resultList[0].LinkedTransaction!.MerchantName.Should().Be("Netflix");
    }

    #endregion

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ReturnsSubscription_WithAllNavigationProperties()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var account = CreateTestAccount(1, user.Id);
        await context.Accounts.AddAsync(account);

        var transaction = CreateTestTransaction(1, account.Id, 15.99m, "Netflix");
        await context.Transactions.AddAsync(transaction);

        var subscription = new Subscription
        {
            UserId = user.Id,
            Name = "Netflix",
            Amount = 15.99m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = DateTime.UtcNow.AddDays(30),
            Status = SubscriptionStatus.Active,
            Category = SubscriptionCategory.Streaming,
            LinkedTransactionId = transaction.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await context.Subscriptions.AddAsync(subscription);
        await context.SaveChangesAsync();

        // Create related subscription transactions
        var subscriptionTransaction = new SubscriptionTransaction
        {
            SubscriptionId = subscription.Id,
            TransactionId = transaction.Id,
            CreatedAt = DateTime.UtcNow
        };
        await context.SubscriptionTransactions.AddAsync(subscriptionTransaction);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByIdAsync(subscription.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(subscription.Id);
        result.User.Should().NotBeNull();
        result.User.Id.Should().Be(user.Id);
        result.LinkedTransaction.Should().NotBeNull();
        result.LinkedTransaction!.Id.Should().Be(transaction.Id);
        result.SubscriptionTransactions.Should().HaveCount(1);
        result.SubscriptionTransactions.First().Transaction.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenSubscriptionNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        // Act
        var result = await repository.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsSubscription_AndSetsTimestamps()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);
        await context.SaveChangesAsync();

        var beforeCreate = DateTime.UtcNow;

        var subscription = new Subscription
        {
            UserId = user.Id,
            Name = "GitHub Pro",
            Description = "Professional developer tools",
            Amount = 7.00m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = DateTime.UtcNow.AddMonths(1),
            Status = SubscriptionStatus.Active,
            Category = SubscriptionCategory.Professional,
            MerchantName = "GitHub"
        };

        // Act
        var result = await repository.CreateAsync(subscription);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().BeGreaterThan(0);
        result.UserId.Should().Be(user.Id);
        result.Name.Should().Be("GitHub Pro");
        result.Amount.Should().Be(7.00m);
        result.CreatedAt.Should().BeOnOrAfter(beforeCreate);
        result.UpdatedAt.Should().BeOnOrAfter(beforeCreate);
        result.CreatedAt.Should().BeCloseTo(result.UpdatedAt, TimeSpan.FromSeconds(1));

        // Verify it's in the database
        var dbSubscription = await context.Subscriptions.FindAsync(result.Id);
        dbSubscription.Should().NotBeNull();
        dbSubscription!.Name.Should().Be("GitHub Pro");
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesSubscription_AndUpdatesTimestamp()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var originalDate = DateTime.UtcNow.AddHours(-1);
        var subscription = new Subscription
        {
            UserId = user.Id,
            Name = "Netflix Basic",
            Amount = 9.99m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = DateTime.UtcNow.AddDays(30),
            Status = SubscriptionStatus.Active,
            Category = SubscriptionCategory.Streaming,
            CreatedAt = originalDate,
            UpdatedAt = originalDate
        };

        await context.Subscriptions.AddAsync(subscription);
        await context.SaveChangesAsync();

        // Detach to simulate fresh fetch
        context.Entry(subscription).State = EntityState.Detached;

        // Modify the subscription
        var subscriptionToUpdate = await context.Subscriptions.FindAsync(subscription.Id);
        subscriptionToUpdate.Should().NotBeNull();
        subscriptionToUpdate!.Name = "Netflix Premium";
        subscriptionToUpdate.Amount = 19.99m;
        subscriptionToUpdate.Status = SubscriptionStatus.Active;

        var beforeUpdate = DateTime.UtcNow;

        // Act
        var result = await repository.UpdateAsync(subscriptionToUpdate);

        // Assert
        result.Name.Should().Be("Netflix Premium");
        result.Amount.Should().Be(19.99m);
        result.UpdatedAt.Should().BeOnOrAfter(beforeUpdate);
        result.UpdatedAt.Should().BeAfter(result.CreatedAt);

        // Verify in database
        var dbSubscription = await context.Subscriptions.FindAsync(subscription.Id);
        dbSubscription!.Name.Should().Be("Netflix Premium");
        dbSubscription.Amount.Should().Be(19.99m);
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_RemovesSubscription()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var subscription = new Subscription
        {
            UserId = user.Id,
            Name = "Gym Membership",
            Amount = 49.99m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = DateTime.UtcNow.AddDays(15),
            Status = SubscriptionStatus.Active,
            Category = SubscriptionCategory.Fitness,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await context.Subscriptions.AddAsync(subscription);
        await context.SaveChangesAsync();

        var subscriptionId = subscription.Id;

        // Act
        await repository.DeleteAsync(subscriptionId);

        // Assert
        var deletedSubscription = await context.Subscriptions.FindAsync(subscriptionId);
        deletedSubscription.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow_WhenSubscriptionNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        // Act & Assert
        var act = async () => await repository.DeleteAsync(999);
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region GetByUserIdAndDateRangeAsync Tests

    [Fact]
    public async Task GetByUserIdAndDateRangeAsync_ReturnsSubscriptionsInRange()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var startDate = DateTime.UtcNow;
        var endDate = startDate.AddDays(30);

        var subscriptions = new[]
        {
            new Subscription
            {
                UserId = user.Id,
                Name = "Within Range 1",
                Amount = 10.00m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = startDate.AddDays(5),
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Streaming,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Subscription
            {
                UserId = user.Id,
                Name = "Within Range 2",
                Amount = 15.00m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = startDate.AddDays(25),
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Software,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Subscription
            {
                UserId = user.Id,
                Name = "Before Range",
                Amount = 20.00m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = startDate.AddDays(-5), // Before start
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Utilities,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Subscription
            {
                UserId = user.Id,
                Name = "After Range",
                Amount = 25.00m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = endDate.AddDays(5), // After end
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Gaming,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        await context.Subscriptions.AddRangeAsync(subscriptions);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAndDateRangeAsync(user.Id, startDate, endDate);

        // Assert
        var resultList = result.ToList();
        resultList.Should().HaveCount(2);
        resultList.Should().Contain(s => s.Name == "Within Range 1");
        resultList.Should().Contain(s => s.Name == "Within Range 2");
        resultList.Should().NotContain(s => s.Name == "Before Range");
        resultList.Should().NotContain(s => s.Name == "After Range");
        resultList.Should().BeInAscendingOrder(s => s.NextDueDate);
    }

    [Fact]
    public async Task GetByUserIdAndDateRangeAsync_ReturnsEmptyList_WhenNoSubscriptionsInRange()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        var startDate = DateTime.UtcNow.AddDays(100);
        var endDate = startDate.AddDays(30);

        // Act
        var result = await repository.GetByUserIdAndDateRangeAsync(1, startDate, endDate);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByUserIdAndDateRangeAsync_IncludesNavigationProperties()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var account = CreateTestAccount(1, user.Id);
        await context.Accounts.AddAsync(account);

        var transaction = CreateTestTransaction(1, account.Id, 15.99m, "Netflix");
        await context.Transactions.AddAsync(transaction);

        var startDate = DateTime.UtcNow;
        var endDate = startDate.AddDays(30);

        var subscription = new Subscription
        {
            UserId = user.Id,
            Name = "Netflix",
            Amount = 15.99m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = startDate.AddDays(15),
            Status = SubscriptionStatus.Active,
            Category = SubscriptionCategory.Streaming,
            LinkedTransactionId = transaction.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await context.Subscriptions.AddAsync(subscription);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAndDateRangeAsync(user.Id, startDate, endDate);

        // Assert
        var resultList = result.ToList();
        resultList.Should().HaveCount(1);
        resultList[0].User.Should().NotBeNull();
        resultList[0].LinkedTransaction.Should().NotBeNull();
    }

    #endregion

    #region GetActiveByUserIdAsync Tests

    [Fact]
    public async Task GetActiveByUserIdAsync_ReturnsOnlyActiveSubscriptions()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var subscriptions = new[]
        {
            new Subscription
            {
                UserId = user.Id,
                Name = "Active Subscription 1",
                Amount = 10.00m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = DateTime.UtcNow.AddDays(10),
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Streaming,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Subscription
            {
                UserId = user.Id,
                Name = "Active Subscription 2",
                Amount = 15.00m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = DateTime.UtcNow.AddDays(5),
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Software,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Subscription
            {
                UserId = user.Id,
                Name = "Paused Subscription",
                Amount = 20.00m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = DateTime.UtcNow.AddDays(20),
                Status = SubscriptionStatus.Paused,
                Category = SubscriptionCategory.Fitness,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Subscription
            {
                UserId = user.Id,
                Name = "Cancelled Subscription",
                Amount = 25.00m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = DateTime.UtcNow.AddDays(-5),
                Status = SubscriptionStatus.Cancelled,
                Category = SubscriptionCategory.Gaming,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Subscription
            {
                UserId = user.Id,
                Name = "Expired Subscription",
                Amount = 30.00m,
                Frequency = SubscriptionFrequency.Annual,
                NextDueDate = DateTime.UtcNow.AddDays(-30),
                Status = SubscriptionStatus.Expired,
                Category = SubscriptionCategory.News,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        await context.Subscriptions.AddRangeAsync(subscriptions);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetActiveByUserIdAsync(user.Id);

        // Assert
        var resultList = result.ToList();
        resultList.Should().HaveCount(2);
        resultList.Should().AllSatisfy(s => s.Status.Should().Be(SubscriptionStatus.Active));
        resultList.Should().Contain(s => s.Name == "Active Subscription 1");
        resultList.Should().Contain(s => s.Name == "Active Subscription 2");
        resultList.Should().NotContain(s => s.Name == "Paused Subscription");
        resultList.Should().NotContain(s => s.Name == "Cancelled Subscription");
        resultList.Should().NotContain(s => s.Name == "Expired Subscription");
        resultList.Should().BeInAscendingOrder(s => s.NextDueDate);
    }

    [Fact]
    public async Task GetActiveByUserIdAsync_ReturnsEmptyList_WhenNoActiveSubscriptions()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        var user = CreateTestUser();
        await context.Users.AddAsync(user);

        var subscription = new Subscription
        {
            UserId = user.Id,
            Name = "Cancelled Subscription",
            Amount = 15.00m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = DateTime.UtcNow.AddDays(10),
            Status = SubscriptionStatus.Cancelled,
            Category = SubscriptionCategory.Streaming,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await context.Subscriptions.AddAsync(subscription);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetActiveByUserIdAsync(user.Id);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveByUserIdAsync_FiltersCorrectlyByUserId()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SubscriptionRepository(context);

        var user1 = CreateTestUser(1);
        var user2 = CreateTestUser(2);
        await context.Users.AddRangeAsync(user1, user2);

        var subscriptions = new[]
        {
            new Subscription
            {
                UserId = user1.Id,
                Name = "User1 Active",
                Amount = 10.00m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = DateTime.UtcNow.AddDays(10),
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Streaming,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Subscription
            {
                UserId = user2.Id,
                Name = "User2 Active",
                Amount = 15.00m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = DateTime.UtcNow.AddDays(5),
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Software,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        await context.Subscriptions.AddRangeAsync(subscriptions);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetActiveByUserIdAsync(user1.Id);

        // Assert
        var resultList = result.ToList();
        resultList.Should().HaveCount(1);
        resultList[0].Name.Should().Be("User1 Active");
        resultList[0].UserId.Should().Be(user1.Id);
    }

    #endregion
}
