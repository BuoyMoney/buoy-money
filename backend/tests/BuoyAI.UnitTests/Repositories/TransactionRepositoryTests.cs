using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.Repositories;

public class TransactionRepositoryTests
{
    private static BuoyAIDbContext GetInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new BuoyAIDbContext(options);
    }

    private static User CreateTestUser(int id, string username)
    {
        return new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static Account CreateTestAccount(int id, int userId)
    {
        return new Account
        {
            Id = id,
            UserId = userId,
            PlaidAccountId = $"plaid_account_{id}",
            Name = $"Account {id}",
            Type = AccountType.Checking,
            CurrentBalance = 1000m,
            AvailableBalance = 950m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static Transaction CreateTestTransaction(int id, int accountId, decimal amount, DateTime date)
    {
        return new Transaction
        {
            Id = id,
            AccountId = accountId,
            PlaidTransactionId = $"plaid_txn_{id}",
            Amount = amount,
            Type = amount < 0 ? TransactionType.Debit : TransactionType.Credit,
            Description = $"Transaction {id}",
            MerchantName = $"Merchant {id}",
            CategoryId = (int)TransactionCategory.Groceries,
            Date = date,
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            UpdatedAt = DateTime.UtcNow.AddDays(-5)
        };
    }

    #region GetByAccountIdAsync Tests

    [Fact]
    public async Task GetByAccountIdAsync_ReturnsTransactions_OrderedByDateDescending()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);

        var txn1 = CreateTestTransaction(1, 1, -50m, DateTime.UtcNow.AddDays(-10));
        var txn2 = CreateTestTransaction(2, 1, -30m, DateTime.UtcNow.AddDays(-5));
        var txn3 = CreateTestTransaction(3, 1, -20m, DateTime.UtcNow.AddDays(-1));

        context.Transactions.AddRange(txn1, txn2, txn3);
        await context.SaveChangesAsync();

        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.GetByAccountIdAsync(1);

        // Assert
        var transactions = result.ToList();
        transactions.Should().HaveCount(3);
        transactions[0].Id.Should().Be(3); // Most recent first
        transactions[1].Id.Should().Be(2);
        transactions[2].Id.Should().Be(1);
    }

    [Fact]
    public async Task GetByAccountIdAsync_IncludesAccount()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        account.Name = "Test Checking";
        context.Users.Add(user);
        context.Accounts.Add(account);

        var transaction = CreateTestTransaction(1, 1, -100m, DateTime.UtcNow);
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.GetByAccountIdAsync(1);

        // Assert
        var transactions = result.ToList();
        transactions[0].Account.Should().NotBeNull();
        transactions[0].Account.Name.Should().Be("Test Checking");
    }

    [Fact]
    public async Task GetByAccountIdAsync_ReturnsEmpty_WhenNoTransactions()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.GetByAccountIdAsync(999);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region GetByUserIdAsync Tests

    [Fact]
    public async Task GetByUserIdAsync_ReturnsAllUserTransactions()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account1 = CreateTestAccount(1, 1);
        var account2 = CreateTestAccount(2, 1);
        context.Users.Add(user);
        context.Accounts.AddRange(account1, account2);

        var txn1 = CreateTestTransaction(1, 1, -50m, DateTime.UtcNow.AddDays(-5));
        var txn2 = CreateTestTransaction(2, 2, -30m, DateTime.UtcNow.AddDays(-3));
        context.Transactions.AddRange(txn1, txn2);
        await context.SaveChangesAsync();

        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(1);

        // Assert
        var transactions = result.ToList();
        transactions.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetByUserIdAsync_FiltersByStartDate()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);

        var txn1 = CreateTestTransaction(1, 1, -50m, DateTime.UtcNow.AddDays(-10));
        var txn2 = CreateTestTransaction(2, 1, -30m, DateTime.UtcNow.AddDays(-5));
        var txn3 = CreateTestTransaction(3, 1, -20m, DateTime.UtcNow.AddDays(-1));
        context.Transactions.AddRange(txn1, txn2, txn3);
        await context.SaveChangesAsync();

        var repository = new TransactionRepository(context);

        // Act
        var startDate = DateTime.UtcNow.AddDays(-6);
        var result = await repository.GetByUserIdAsync(1, startDate: startDate);

        // Assert
        var transactions = result.ToList();
        transactions.Should().HaveCount(2); // Only txn2 and txn3
        transactions.Should().OnlyContain(t => t.Date >= startDate);
    }

    [Fact]
    public async Task GetByUserIdAsync_FiltersByEndDate()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);

        var txn1 = CreateTestTransaction(1, 1, -50m, DateTime.UtcNow.AddDays(-10));
        var txn2 = CreateTestTransaction(2, 1, -30m, DateTime.UtcNow.AddDays(-5));
        var txn3 = CreateTestTransaction(3, 1, -20m, DateTime.UtcNow.AddDays(-1));
        context.Transactions.AddRange(txn1, txn2, txn3);
        await context.SaveChangesAsync();

        var repository = new TransactionRepository(context);

        // Act
        var endDate = DateTime.UtcNow.AddDays(-4);
        var result = await repository.GetByUserIdAsync(1, endDate: endDate);

        // Assert
        var transactions = result.ToList();
        transactions.Should().HaveCount(2); // Only txn1 and txn2
        transactions.Should().OnlyContain(t => t.Date <= endDate);
    }

    [Fact]
    public async Task GetByUserIdAsync_FiltersByDateRange()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);

        var txn1 = CreateTestTransaction(1, 1, -50m, DateTime.UtcNow.AddDays(-10));
        var txn2 = CreateTestTransaction(2, 1, -30m, DateTime.UtcNow.AddDays(-7));
        var txn3 = CreateTestTransaction(3, 1, -20m, DateTime.UtcNow.AddDays(-3));
        context.Transactions.AddRange(txn1, txn2, txn3);
        await context.SaveChangesAsync();

        var repository = new TransactionRepository(context);

        // Act
        var startDate = DateTime.UtcNow.AddDays(-8);
        var endDate = DateTime.UtcNow.AddDays(-4);
        var result = await repository.GetByUserIdAsync(1, startDate, endDate);

        // Assert
        var transactions = result.ToList();
        transactions.Should().HaveCount(1); // Only txn2
        transactions[0].Id.Should().Be(2);
    }

    [Fact]
    public async Task GetByUserIdAsync_IncludesAccount()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);

        var transaction = CreateTestTransaction(1, 1, -50m, DateTime.UtcNow);
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(1);

        // Assert
        var transactions = result.ToList();
        transactions[0].Account.Should().NotBeNull();
    }

    #endregion

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ReturnsTransaction_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);

        var transaction = CreateTestTransaction(1, 1, -75m, DateTime.UtcNow);
        transaction.Description = "Test Purchase";
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Description.Should().Be("Test Purchase");
        result.Amount.Should().Be(-75m);
    }

    [Fact]
    public async Task GetByIdAsync_IncludesAccount()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);

        var transaction = CreateTestTransaction(1, 1, -50m, DateTime.UtcNow);
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Account.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetByPlaidTransactionIdAsync Tests

    [Fact]
    public async Task GetByPlaidTransactionIdAsync_ReturnsTransaction_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);

        var transaction = CreateTestTransaction(1, 1, -50m, DateTime.UtcNow);
        transaction.PlaidTransactionId = "unique_plaid_id_123";
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.GetByPlaidTransactionIdAsync("unique_plaid_id_123");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.PlaidTransactionId.Should().Be("unique_plaid_id_123");
    }

    [Fact]
    public async Task GetByPlaidTransactionIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.GetByPlaidTransactionIdAsync("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsTransaction_AndSetsTimestamps()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var transaction = new Transaction
        {
            AccountId = 1,
            PlaidTransactionId = "new_plaid_txn",
            Amount = -125m,
            Type = TransactionType.Debit,
            Description = "New Transaction",
            MerchantName = "New Merchant",
            CategoryId = (int)TransactionCategory.Shopping,
            Date = DateTime.UtcNow
        };

        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.CreateAsync(transaction);

        // Assert
        result.Id.Should().BeGreaterThan(0);
        result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));

        var saved = await context.Transactions.FindAsync(result.Id);
        saved.Should().NotBeNull();
        saved!.Description.Should().Be("New Transaction");
    }

    [Fact]
    public async Task CreateAsync_SetsDefaultValues()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var transaction = new Transaction
        {
            AccountId = 1,
            PlaidTransactionId = "default_txn",
            Amount = -50m,
            Date = DateTime.UtcNow
        };

        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.CreateAsync(transaction);

        // Assert
        result.CategoryId.Should().BeNull(); // CategoryId is nullable and not set by repository
        result.IsUserCategorized.Should().BeFalse();
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesTransaction_AndSetsUpdatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);

        var transaction = CreateTestTransaction(1, 1, -50m, DateTime.UtcNow);
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        context.Entry(transaction).State = EntityState.Detached;

        var repository = new TransactionRepository(context);

        // Act
        await Task.Delay(100);
        transaction.Description = "Updated Description";
        transaction.CategoryId = (int)TransactionCategory.Entertainment;
        transaction.IsUserCategorized = true;
        var result = await repository.UpdateAsync(transaction);

        // Assert
        result.Description.Should().Be("Updated Description");
        result.CategoryId.Should().Be((int)TransactionCategory.Entertainment);
        result.IsUserCategorized.Should().BeTrue();
        result.UpdatedAt.Should().BeAfter(result.CreatedAt);

        var updated = await context.Transactions.FindAsync(1);
        updated!.Description.Should().Be("Updated Description");
    }

    #endregion

    #region CreateManyAsync Tests

    [Fact]
    public async Task CreateManyAsync_AddsMultipleTransactions()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var transactions = new List<Transaction>
        {
            new Transaction
            {
                AccountId = 1,
                PlaidTransactionId = "batch_1",
                Amount = -50m,
                Description = "Batch Transaction 1",
                Date = DateTime.UtcNow
            },
            new Transaction
            {
                AccountId = 1,
                PlaidTransactionId = "batch_2",
                Amount = -30m,
                Description = "Batch Transaction 2",
                Date = DateTime.UtcNow
            },
            new Transaction
            {
                AccountId = 1,
                PlaidTransactionId = "batch_3",
                Amount = 100m,
                Description = "Batch Transaction 3",
                Date = DateTime.UtcNow
            }
        };

        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.CreateManyAsync(transactions);

        // Assert
        var created = result.ToList();
        created.Should().HaveCount(3);
        created.Should().OnlyContain(t => t.Id > 0);

        var saved = await context.Transactions.ToListAsync();
        saved.Should().HaveCount(3);
    }

    [Fact]
    public async Task CreateManyAsync_SetsTimestampsForAll()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var transactions = new List<Transaction>
        {
            new Transaction
            {
                AccountId = 1,
                PlaidTransactionId = "ts_1",
                Amount = -25m,
                Date = DateTime.UtcNow
            },
            new Transaction
            {
                AccountId = 1,
                PlaidTransactionId = "ts_2",
                Amount = -35m,
                Date = DateTime.UtcNow
            }
        };

        var repository = new TransactionRepository(context);

        // Act
        var result = await repository.CreateManyAsync(transactions);

        // Assert
        var created = result.ToList();
        created.Should().OnlyContain(t => 
            t.CreatedAt != default && 
            t.UpdatedAt != default &&
            t.CreatedAt == t.UpdatedAt);
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_RemovesTransaction()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        var account = CreateTestAccount(1, 1);
        context.Users.Add(user);
        context.Accounts.Add(account);

        var transaction = CreateTestTransaction(1, 1, -50m, DateTime.UtcNow);
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();

        var repository = new TransactionRepository(context);

        // Act
        await repository.DeleteAsync(1);

        // Assert
        var deleted = await context.Transactions.FindAsync(1);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow_WhenTransactionNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new TransactionRepository(context);

        // Act & Assert
        await repository.Invoking(r => r.DeleteAsync(999))
            .Should().NotThrowAsync();
    }

    #endregion

    #region DeleteAllByUserIdAsync Tests

    [Fact]
    public async Task DeleteAllByUserIdAsync_RemovesAllUserTransactions()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user1 = CreateTestUser(1, "user1");
        var user2 = CreateTestUser(2, "user2");
        var account1 = CreateTestAccount(1, 1);
        var account2 = CreateTestAccount(2, 2);
        context.Users.AddRange(user1, user2);
        context.Accounts.AddRange(account1, account2);

        var txn1 = CreateTestTransaction(1, 1, -50m, DateTime.UtcNow);
        var txn2 = CreateTestTransaction(2, 1, -30m, DateTime.UtcNow);
        var txn3 = CreateTestTransaction(3, 2, -20m, DateTime.UtcNow);
        context.Transactions.AddRange(txn1, txn2, txn3);
        await context.SaveChangesAsync();

        var repository = new TransactionRepository(context);

        // Act
        await repository.DeleteAllByUserIdAsync(1);

        // Assert
        var remaining = await context.Transactions.ToListAsync();
        remaining.Should().HaveCount(1);
        remaining[0].AccountId.Should().Be(2); // Only user2's transaction remains
    }

    [Fact]
    public async Task DeleteAllByUserIdAsync_DoesNotThrow_WhenNoTransactions()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new TransactionRepository(context);

        // Act & Assert
        await repository.Invoking(r => r.DeleteAllByUserIdAsync(999))
            .Should().NotThrowAsync();
    }

    #endregion
}
