using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.DTOs.Category;
using BuoyAI.Core.DTOs.Import;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Services;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using BuoyAI.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuoyAI.UnitTests.Services.Import;

/// <summary>
/// Wires the real ImportService, ingest service and repositories over an in-memory database.
/// The categorizer, category service, budget service and family authorization are mocks.
/// </summary>
internal sealed class ImportHarness : IDisposable
{
  public BuoyAIDbContext Db { get; }
  public ImportService Service { get; }
  public Mock<ITransactionIngestCategorizer> Categorizer { get; } = new();
  public Mock<IBudgetService> Budgets { get; } = new();
  public Mock<IFamilyAuthorizationService> Family { get; } = new();
  public List<CreateBudgetRequest> BudgetRequests { get; } = new();
  public List<CategoryDto> CreatedCategories { get; } = new();

  private int _nextCategoryId = 1000;

  public ImportHarness()
  {
    Db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString())
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)).Options);

    Categorizer
      .Setup(c => c.ProcessIngestedAsync(It.IsAny<IReadOnlyCollection<Transaction>>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(new IngestCategorizationResult());

    Family.Setup(f => f.CanModifyFinancialDataAsync(It.IsAny<int>())).ReturnsAsync(true);

    Budgets
      .Setup(b => b.CreateBudgetAsync(It.IsAny<int>(), It.IsAny<CreateBudgetRequest>()))
      .ReturnsAsync((int _, CreateBudgetRequest r) =>
      {
        BudgetRequests.Add(r);
        return new BudgetDto { Id = 77, Name = r.Name };
      });

    var categories = new Mock<ICategoryService>();
    categories.Setup(c => c.GetAllCategoriesAsync(It.IsAny<int>())).ReturnsAsync(() => new CategoriesResponseDto
    {
      SystemCategories = new List<CategoryDto>
      {
        new() { Id = 1, Name = "Salary", IsIncome = true, IsSystem = true },
        new() { Id = 4, Name = "Other Income", IsIncome = true, IsSystem = true },
        new() { Id = 20, Name = "Groceries", IsSystem = true },
        new() { Id = 21, Name = "Dining Out", IsSystem = true },
        new() { Id = 23, Name = "Entertainment", IsSystem = true },
        new() { Id = 31, Name = "Transfer", IsSystem = true },
        new() { Id = 99, Name = "Uncategorized", IsSystem = true }
      },
      PersonalCategories = CreatedCategories.ToList(),
      FamilyCategories = new List<CategoryDto>()
    });
    categories.Setup(c => c.CreateCategoryAsync(It.IsAny<CreateCategoryDto>(), It.IsAny<int>()))
      .ReturnsAsync((CreateCategoryDto dto, int _) =>
      {
        var created = new CategoryDto { Id = _nextCategoryId++, Name = dto.Name, IsIncome = dto.IsIncome, Scope = "personal" };
        CreatedCategories.Add(created);
        return created;
      });
    categories.Setup(c => c.ValidateCategoryAccessAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(true);

    var transactionRepository = new TransactionRepository(Db);
    var accountRepository = new AccountRepository(Db);
    var ingest = new TransactionIngestService(
      transactionRepository, accountRepository, Categorizer.Object, NullLogger<TransactionIngestService>.Instance);

    Service = new ImportService(
      Db, ingest, categories.Object, Budgets.Object, Family.Object,
      new MemoryCache(new MemoryCacheOptions()), NullLogger<ImportService>.Instance);
  }

  public User AddUser(int id, int? familyId = null)
  {
    var user = new User { Id = id, Username = $"user{id}", Email = $"u{id}@example.test", FamilyId = familyId, Currency = "USD" };
    Db.Users.Add(user);
    Db.SaveChanges();
    return user;
  }

  public Account AddAccount(int userId, string name, string plaidId, int? familyId = null, AccountType type = AccountType.Checking)
  {
    var account = new Account
    {
      UserId = userId,
      FamilyId = familyId,
      Name = name,
      PlaidAccountId = plaidId,
      Type = type,
      Currency = "USD",
      IsActive = true
    };
    Db.Accounts.Add(account);
    Db.SaveChanges();
    return account;
  }

  public Transaction AddTransaction(int accountId, string externalId, decimal amount, DateTime date, string description, string? merchant = null)
  {
    var tx = new Transaction
    {
      AccountId = accountId,
      PlaidTransactionId = externalId,
      Amount = amount,
      Type = amount >= 0 ? TransactionType.Credit : TransactionType.Debit,
      Date = DateTime.SpecifyKind(date, DateTimeKind.Utc),
      Description = description,
      MerchantName = merchant ?? description,
      CategoryId = 99
    };
    Db.Transactions.Add(tx);
    Db.SaveChanges();
    return tx;
  }

  public static ImportFileInput Csv(string name, string content) =>
    new() { FileName = name, Content = Encoding.UTF8.GetBytes(content) };

  public Task<ImportPreviewResult> PreviewCsvAsync(int userId, string csv, int? accountId = null, ImportMapping? mapping = null, string fileName = "bank.csv") =>
    Service.PreviewAsync(userId, new[] { Csv(fileName, csv) }, new ImportPreviewRequest { AccountId = accountId, Mapping = mapping ?? new ImportMapping() });

  public void Dispose() => Db.Dispose();
}
