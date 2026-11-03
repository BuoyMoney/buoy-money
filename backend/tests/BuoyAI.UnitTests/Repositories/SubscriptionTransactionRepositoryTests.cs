using Microsoft.EntityFrameworkCore;
using FluentAssertions;
using Xunit;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;

namespace BuoyAI.UnitTests.Repositories;

public class SubscriptionTransactionRepositoryTests
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

    private Account CreateTestAccount(int userId, int plaidConnectionId)
    {
        return new Account
        {
            UserId = userId,
            PlaidConnectionId = plaidConnectionId,
            PlaidAccountId = Guid.NewGuid().ToString(),
            Name = "Test Account",
            Type = AccountType.Checking,
            CreatedAt = DateTime.UtcNow
        };
    }

    private PlaidConnection CreateTestConnection(int userId)
    {
        return new PlaidConnection
        {
            UserId = userId,
            ItemId = Guid.NewGuid().ToString(),
            AccessToken = "test-token",
            InstitutionName = "Test Bank",
            CreatedAt = DateTime.UtcNow
        };
    }

    private Transaction CreateTestTransaction(int accountId, string description = "Test Transaction")
    {
        return new Transaction
        {
            AccountId = accountId,
            PlaidTransactionId = Guid.NewGuid().ToString(),
            Description = description,
            Amount = 50.00m,
            Type = TransactionType.Debit,
            Date = DateTime.UtcNow.Date,
            CreatedAt = DateTime.UtcNow
        };
    }

    private Subscription CreateTestSubscription(int userId, string name = "Netflix")
    {
        return new Subscription
        {
            UserId = userId,
            Name = name,
            Amount = 15.99m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = DateTime.UtcNow.AddDays(30),
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
    }

    #region GetBySubscriptionIdAsync Tests

    [Fact]
    public async Task GetBySubscriptionIdAsync_ReturnsSubscriptionTransactions_OrderedByCreatedAtDescending()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new SubscriptionTransactionRepository(context);

        var user = CreateTestUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var connection = CreateTestConnection(user.Id);
        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        var account = CreateTestAccount(user.Id, connection.Id);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var subscription = CreateTestSubscription(user.Id);
        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();

        var transaction1 = CreateTestTransaction(account.Id, "Netflix Jan");
        transaction1.CreatedAt = DateTime.UtcNow.AddDays(-2);
        var transaction2 = CreateTestTransaction(account.Id, "Netflix Feb");
        transaction2.CreatedAt = DateTime.UtcNow.AddDays(-1);
        context.Transactions.AddRange(transaction1, transaction2);
        await context.SaveChangesAsync();

        var subTrans1 = new SubscriptionTransaction
        {
            SubscriptionId = subscription.Id,
            TransactionId = transaction1.Id,
            CreatedAt = transaction1.CreatedAt
        };
        var subTrans2 = new SubscriptionTransaction
        {
            SubscriptionId = subscription.Id,
            TransactionId = transaction2.Id,
            CreatedAt = transaction2.CreatedAt
        };
        context.SubscriptionTransactions.AddRange(subTrans1, subTrans2);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetBySubscriptionIdAsync(subscription.Id);

        // Assert
        var list = result.ToList();
        list.Should().HaveCount(2);
        list[0].TransactionId.Should().Be(transaction2.Id); // Most recent first
        list[1].TransactionId.Should().Be(transaction1.Id);
    }

    [Fact]
    public async Task GetBySubscriptionIdAsync_IncludesTransactionAndSubscription()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new SubscriptionTransactionRepository(context);

        var user = CreateTestUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var connection = CreateTestConnection(user.Id);
        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        var account = CreateTestAccount(user.Id, connection.Id);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var subscription = CreateTestSubscription(user.Id, "Spotify");
        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();

        var transaction = CreateTestTransaction(account.Id, "Spotify Premium");
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var subTrans = new SubscriptionTransaction
        {
            SubscriptionId = subscription.Id,
            TransactionId = transaction.Id
        };
        context.SubscriptionTransactions.Add(subTrans);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetBySubscriptionIdAsync(subscription.Id);

        // Assert
        var list = result.ToList();
        list.Should().HaveCount(1);
        list[0].Transaction.Should().NotBeNull();
        list[0].Transaction.Description.Should().Be("Spotify Premium");
        list[0].Subscription.Should().NotBeNull();
        list[0].Subscription.Name.Should().Be("Spotify");
    }

    [Fact]
    public async Task GetBySubscriptionIdAsync_ReturnsEmpty_WhenNoTransactions()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new SubscriptionTransactionRepository(context);

        var user = CreateTestUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var subscription = CreateTestSubscription(user.Id);
        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetBySubscriptionIdAsync(subscription.Id);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsSubscriptionTransaction_AndSetsCreatedAt()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new SubscriptionTransactionRepository(context);

        var user = CreateTestUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var connection = CreateTestConnection(user.Id);
        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        var account = CreateTestAccount(user.Id, connection.Id);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var subscription = CreateTestSubscription(user.Id);
        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();

        var transaction = CreateTestTransaction(account.Id);
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var subTrans = new SubscriptionTransaction
        {
            SubscriptionId = subscription.Id,
            TransactionId = transaction.Id
        };

        var beforeCreate = DateTime.UtcNow;

        // Act
        var result = await repository.CreateAsync(subTrans);

        // Assert
        result.Id.Should().BeGreaterThan(0);
        result.CreatedAt.Should().BeOnOrAfter(beforeCreate);
        result.CreatedAt.Should().BeOnOrBefore(DateTime.UtcNow);
    }

    #endregion

    #region DeleteBySubscriptionIdAsync Tests

    [Fact]
    public async Task DeleteBySubscriptionIdAsync_RemovesAllSubscriptionTransactions()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new SubscriptionTransactionRepository(context);

        var user = CreateTestUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var connection = CreateTestConnection(user.Id);
        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        var account = CreateTestAccount(user.Id, connection.Id);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var subscription = CreateTestSubscription(user.Id);
        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();

        var transaction1 = CreateTestTransaction(account.Id, "Payment 1");
        var transaction2 = CreateTestTransaction(account.Id, "Payment 2");
        context.Transactions.AddRange(transaction1, transaction2);
        await context.SaveChangesAsync();

        var subTrans1 = new SubscriptionTransaction
        {
            SubscriptionId = subscription.Id,
            TransactionId = transaction1.Id,
            CreatedAt = DateTime.UtcNow
        };
        var subTrans2 = new SubscriptionTransaction
        {
            SubscriptionId = subscription.Id,
            TransactionId = transaction2.Id,
            CreatedAt = DateTime.UtcNow
        };
        context.SubscriptionTransactions.AddRange(subTrans1, subTrans2);
        await context.SaveChangesAsync();

        // Act
        await repository.DeleteBySubscriptionIdAsync(subscription.Id);

        // Assert
        var remaining = await context.SubscriptionTransactions
            .Where(st => st.SubscriptionId == subscription.Id)
            .ToListAsync();
        remaining.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteBySubscriptionIdAsync_DoesNotThrow_WhenNoTransactions()
    {
        // Arrange
        using var context = CreateDbContext();
        var repository = new SubscriptionTransactionRepository(context);

        var user = CreateTestUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var subscription = CreateTestSubscription(user.Id);
        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();

        // Act & Assert
        await repository.Invoking(r => r.DeleteBySubscriptionIdAsync(subscription.Id))
            .Should().NotThrowAsync();
    }

    #endregion
}
