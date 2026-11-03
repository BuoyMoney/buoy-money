using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.Repositories;

public class AccountRepositoryTests
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

    private static Account CreateTestAccount(int id, int userId, string plaidAccountId, bool isActive = true)
    {
        return new Account
        {
            Id = id,
            UserId = userId,
            PlaidAccountId = plaidAccountId,
            Name = $"Account {id}",
            OfficialName = $"Official Account {id}",
            Type = AccountType.Checking,
            Subtype = "checking",
            CurrentBalance = 1000m,
            AvailableBalance = 950m,
            Currency = "USD",
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            UpdatedAt = DateTime.UtcNow.AddDays(-10)
        };
    }

    #region GetByUserIdAsync Tests

    [Fact]
    public async Task GetByUserIdAsync_ReturnsActiveAccounts()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var activeAccount = CreateTestAccount(1, 1, "plaid_active", isActive: true);
        var inactiveAccount = CreateTestAccount(2, 1, "plaid_inactive", isActive: false);

        context.Accounts.AddRange(activeAccount, inactiveAccount);
        await context.SaveChangesAsync();

        var repository = new AccountRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(1);

        // Assert
        var accounts = result.ToList();
        accounts.Should().HaveCount(1);
        accounts[0].PlaidAccountId.Should().Be("plaid_active");
        accounts[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetByUserIdAsync_IncludesTransactions()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var account = CreateTestAccount(1, 1, "plaid_account");
        account.Transactions = new List<Transaction>
        {
            new Transaction
            {
                Id = 1,
                AccountId = 1,
                Amount = -50m,
                Date = DateTime.UtcNow,
                Description = "Test Transaction",
                CategoryId = (int)TransactionCategory.Groceries,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var repository = new AccountRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(1);

        // Assert
        var accounts = result.ToList();
        accounts[0].Transactions.Should().HaveCount(1);
        accounts[0].Transactions.First().Description.Should().Be("Test Transaction");
    }

    [Fact]
    public async Task GetByUserIdAsync_ReturnsEmpty_WhenNoAccounts()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new AccountRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(999);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region GetByConnectionIdAsync Tests

    [Fact]
    public async Task GetByConnectionIdAsync_ReturnsAccountsForConnection()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var account1 = CreateTestAccount(1, 1, "plaid_1");
        account1.PlaidConnectionId = 100;
        var account2 = CreateTestAccount(2, 1, "plaid_2");
        account2.PlaidConnectionId = 100;
        var account3 = CreateTestAccount(3, 1, "plaid_3");
        account3.PlaidConnectionId = 200;

        context.Accounts.AddRange(account1, account2, account3);
        await context.SaveChangesAsync();

        var repository = new AccountRepository(context);

        // Act
        var result = await repository.GetByConnectionIdAsync(100);

        // Assert
        var accounts = result.ToList();
        accounts.Should().HaveCount(2);
        accounts.Should().OnlyContain(a => a.PlaidConnectionId == 100);
    }

    [Fact]
    public async Task GetByConnectionIdAsync_FiltersInactiveAccounts()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var activeAccount = CreateTestAccount(1, 1, "plaid_active", isActive: true);
        activeAccount.PlaidConnectionId = 100;
        var inactiveAccount = CreateTestAccount(2, 1, "plaid_inactive", isActive: false);
        inactiveAccount.PlaidConnectionId = 100;

        context.Accounts.AddRange(activeAccount, inactiveAccount);
        await context.SaveChangesAsync();

        var repository = new AccountRepository(context);

        // Act
        var result = await repository.GetByConnectionIdAsync(100);

        // Assert
        var accounts = result.ToList();
        accounts.Should().HaveCount(1);
        accounts[0].PlaidAccountId.Should().Be("plaid_active");
    }

    [Fact]
    public async Task GetByConnectionIdAsync_IncludesTransactions()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var account = CreateTestAccount(1, 1, "plaid_account");
        account.PlaidConnectionId = 100;
        account.Transactions = new List<Transaction>
        {
            new Transaction
            {
                Id = 1,
                AccountId = 1,
                Amount = -25m,
                Date = DateTime.UtcNow,
                Description = "Connection Transaction",
                CategoryId = (int)TransactionCategory.Shopping,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var repository = new AccountRepository(context);

        // Act
        var result = await repository.GetByConnectionIdAsync(100);

        // Assert
        var accounts = result.ToList();
        accounts[0].Transactions.Should().HaveCount(1);
    }

    #endregion

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ReturnsAccount_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var account = CreateTestAccount(1, 1, "plaid_account");
        account.Type = AccountType.Savings;
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var repository = new AccountRepository(context);

        // Act
        var result = await repository.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.PlaidAccountId.Should().Be("plaid_account");
        result.Type.Should().Be(AccountType.Savings);
    }

    [Fact]
    public async Task GetByIdAsync_IncludesUserAndTransactions()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var account = CreateTestAccount(1, 1, "plaid_account");
        account.Transactions = new List<Transaction>
        {
            new Transaction
            {
                Id = 1,
                AccountId = 1,
                Amount = -100m,
                Date = DateTime.UtcNow,
                Description = "Test",
                CategoryId = (int)TransactionCategory.Utilities,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var repository = new AccountRepository(context);

        // Act
        var result = await repository.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.User.Should().NotBeNull();
        result.User.Username.Should().Be("testuser");
        result.Transactions.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new AccountRepository(context);

        // Act
        var result = await repository.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetByPlaidAccountIdAsync Tests

    [Fact]
    public async Task GetByPlaidAccountIdAsync_ReturnsAccount_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var account = CreateTestAccount(1, 1, "plaid_unique_123");
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var repository = new AccountRepository(context);

        // Act
        var result = await repository.GetByPlaidAccountIdAsync("plaid_unique_123");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.PlaidAccountId.Should().Be("plaid_unique_123");
    }

    [Fact]
    public async Task GetByPlaidAccountIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new AccountRepository(context);

        // Act
        var result = await repository.GetByPlaidAccountIdAsync("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsAccount_AndSetsTimestamps()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var account = new Account
        {
            UserId = 1,
            PlaidAccountId = "new_plaid_account",
            Name = "New Account",
            OfficialName = "New Official Account",
            Type = AccountType.CreditCard,
            Subtype = "credit_card",
            CurrentBalance = 500m,
            AvailableBalance = 450m,
            Currency = "USD",
            IsActive = true
        };

        var repository = new AccountRepository(context);

        // Act
        var result = await repository.CreateAsync(account);

        // Assert
        result.Id.Should().BeGreaterThan(0);
        result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));

        var saved = await context.Accounts.FindAsync(result.Id);
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("New Account");
    }

    [Fact]
    public async Task CreateAsync_SetsDefaultValues()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var account = new Account
        {
            UserId = 1,
            PlaidAccountId = "plaid_defaults",
            Name = "Default Account",
            Type = AccountType.Checking
        };

        var repository = new AccountRepository(context);

        // Act
        var result = await repository.CreateAsync(account);

        // Assert
        result.Currency.Should().Be("USD");
        result.IsActive.Should().BeTrue();
        result.CurrentBalance.Should().Be(0m);
        result.AvailableBalance.Should().Be(0m);
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesAccount_AndSetsUpdatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var account = CreateTestAccount(1, 1, "plaid_account");
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        context.Entry(account).State = EntityState.Detached;

        var repository = new AccountRepository(context);

        // Act
        await Task.Delay(100);
        account.CurrentBalance = 2000m;
        account.AvailableBalance = 1900m;
        account.Name = "Updated Account";
        var result = await repository.UpdateAsync(account);

        // Assert
        result.CurrentBalance.Should().Be(2000m);
        result.AvailableBalance.Should().Be(1900m);
        result.Name.Should().Be("Updated Account");
        result.UpdatedAt.Should().BeAfter(result.CreatedAt);

        var updated = await context.Accounts.FindAsync(1);
        updated!.CurrentBalance.Should().Be(2000m);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesAccountType()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var account = CreateTestAccount(1, 1, "plaid_account");
        account.Type = AccountType.Checking;
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        context.Entry(account).State = EntityState.Detached;

        var repository = new AccountRepository(context);

        // Act
        account.Type = AccountType.Investment;
        account.Subtype = "401k";
        await repository.UpdateAsync(account);

        // Assert
        var updated = await context.Accounts.FindAsync(1);
        updated!.Type.Should().Be(AccountType.Investment);
        updated.Subtype.Should().Be("401k");
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_SoftDeletesAccount_AndSetsUpdatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var account = CreateTestAccount(1, 1, "plaid_account");
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var repository = new AccountRepository(context);

        // Act
        await Task.Delay(100);
        await repository.DeleteAsync(1);

        // Assert
        var deleted = await context.Accounts.FindAsync(1);
        deleted.Should().NotBeNull();
        deleted!.IsActive.Should().BeFalse();
        deleted.UpdatedAt.Should().BeAfter(deleted.CreatedAt);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow_WhenAccountNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new AccountRepository(context);

        // Act & Assert
        await repository.Invoking(r => r.DeleteAsync(999))
            .Should().NotThrowAsync();
    }

    #endregion
}
