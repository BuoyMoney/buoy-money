using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.DTOs.Sync;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Unit tests for the offline-sync batch dispatcher (#493). Underlying services are mocked; a real
/// InMemory DbContext backs the idempotency ledger and conflict lookups.
/// </summary>
public class SyncServiceTests
{
  private const int UserId = 7;

  private readonly Mock<IBudgetService> _budget = new();
  private readonly Mock<ISavingsService> _savings = new();
  private readonly Mock<INotificationService> _notifications = new();
  private readonly Mock<ITransactionService> _transactions = new();
  private readonly Mock<IFamilyAuthorizationService> _auth = new();
  private readonly Mock<IUserRepository> _userRepo = new();
  private readonly BuoyAIDbContext _db;
  private readonly SyncService _service;

  public SyncServiceTests()
  {
    _db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options);

    _service = new SyncService(
      _db, _budget.Object, _savings.Object, _notifications.Object,
      _transactions.Object, _auth.Object, _userRepo.Object,
      new Mock<ILogger<SyncService>>().Object);
  }

  private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

  private static SyncBatchRequest Batch(params SyncOperation[] ops) => new() { Operations = ops.ToList() };

  private static SyncOperation Op(
    string type, string entityId, JsonElement? payload = null,
    string? key = null, DateTime? baseUpdatedAt = null) => new()
  {
    IdempotencyKey = key ?? Guid.NewGuid().ToString(),
    OperationType = type,
    EntityId = entityId,
    Payload = payload,
    BaseUpdatedAt = baseUpdatedAt,
  };

  [Fact]
  public async Task ProcessBatchAsync_BudgetCategoryUpdateAuthorized_ReturnsApplied()
  {
    _budget.Setup(b => b.GetBudgetCategoryAsync(20))
      .ReturnsAsync(new BudgetCategoryDto { Id = 20, BudgetId = 5 });
    _auth.Setup(a => a.CanAccessBudgetAsync(UserId, 5, ResourcePermission.Edit))
      .ReturnsAsync(AuthorizationResult.Allowed());
    _budget.Setup(b => b.UpdateBudgetCategoryAsync(20, It.IsAny<UpdateBudgetCategoryRequest>()))
      .ReturnsAsync(new BudgetCategoryDto { Id = 20, BudgetId = 5 });

    var op = Op(SyncOperationTypes.BudgetCategoryUpdate, "20",
      Json(new { allocatedAmount = 100m, allowRollover = true }));

    var response = await _service.ProcessBatchAsync(UserId, Batch(op));

    response.Results.Single().Status.Should().Be(SyncOperationStatus.Applied);
    _budget.Verify(b => b.UpdateBudgetCategoryAsync(20, It.IsAny<UpdateBudgetCategoryRequest>()), Times.Once);
    (await _db.SyncIdempotencyKeys.CountAsync()).Should().Be(1);
  }

  [Fact]
  public async Task ProcessBatchAsync_BudgetCategoryUpdateUnauthorized_ReturnsFailedAndDoesNotApply()
  {
    _budget.Setup(b => b.GetBudgetCategoryAsync(20))
      .ReturnsAsync(new BudgetCategoryDto { Id = 20, BudgetId = 5 });
    _auth.Setup(a => a.CanAccessBudgetAsync(UserId, 5, ResourcePermission.Edit))
      .ReturnsAsync(AuthorizationResult.Denied("Read-only members cannot edit budgets"));

    var op = Op(SyncOperationTypes.BudgetCategoryUpdate, "20",
      Json(new { allocatedAmount = 100m }));

    var response = await _service.ProcessBatchAsync(UserId, Batch(op));

    response.Results.Single().Status.Should().Be(SyncOperationStatus.Failed);
    _budget.Verify(b => b.UpdateBudgetCategoryAsync(It.IsAny<int>(), It.IsAny<UpdateBudgetCategoryRequest>()), Times.Never);
  }

  [Fact]
  public async Task ProcessBatchAsync_SavingsGoalUpdateAuthorized_ReturnsApplied()
  {
    _auth.Setup(a => a.CanAccessGoalAsync(UserId, 3, ResourcePermission.Edit))
      .ReturnsAsync(AuthorizationResult.Allowed());
    _savings.Setup(s => s.UpdateSavingsGoalAsync(3, It.IsAny<UpdateSavingsGoalRequest>(), UserId))
      .ReturnsAsync(new SavingsGoalDto { Id = 3 });

    var op = Op(SyncOperationTypes.SavingsGoalUpdate, "3",
      Json(new { name = "Trip", description = "", targetAmount = 500m, allocationPercentage = 5m }));

    var response = await _service.ProcessBatchAsync(UserId, Batch(op));

    response.Results.Single().Status.Should().Be(SyncOperationStatus.Applied);
    _savings.Verify(s => s.UpdateSavingsGoalAsync(3, It.IsAny<UpdateSavingsGoalRequest>(), UserId), Times.Once);
  }

  [Fact]
  public async Task ProcessBatchAsync_NotificationMarkRead_ReturnsApplied()
  {
    _notifications.Setup(n => n.MarkAsReadAsync(UserId, 11)).ReturnsAsync(true);

    var response = await _service.ProcessBatchAsync(
      UserId, Batch(Op(SyncOperationTypes.NotificationMarkRead, "11")));

    response.Results.Single().Status.Should().Be(SyncOperationStatus.Applied);
    _notifications.Verify(n => n.MarkAsReadAsync(UserId, 11), Times.Once);
  }

  [Fact]
  public async Task ProcessBatchAsync_NotificationMarkReadNotFound_ReturnsFailed()
  {
    _notifications.Setup(n => n.MarkAsReadAsync(UserId, 11)).ReturnsAsync(false);

    var response = await _service.ProcessBatchAsync(
      UserId, Batch(Op(SyncOperationTypes.NotificationMarkRead, "11")));

    response.Results.Single().Status.Should().Be(SyncOperationStatus.Failed);
  }

  [Fact]
  public async Task ProcessBatchAsync_TransactionNotesUpdate_ReturnsApplied()
  {
    _transactions.Setup(t => t.UpdateTransactionNotesAsync(9, "hello", UserId))
      .ReturnsAsync(new TransactionDto { Id = 9 });

    var op = Op(SyncOperationTypes.TransactionNotesUpdate, "9", Json(new { notes = "hello" }));

    var response = await _service.ProcessBatchAsync(UserId, Batch(op));

    response.Results.Single().Status.Should().Be(SyncOperationStatus.Applied);
    _transactions.Verify(t => t.UpdateTransactionNotesAsync(9, "hello", UserId), Times.Once);
  }

  [Fact]
  public async Task ProcessBatchAsync_UserBudgetSettings_ReturnsAppliedAndPersistsUser()
  {
    var user = new User { Id = UserId };
    _userRepo.Setup(r => r.GetByIdAsync(UserId)).ReturnsAsync(user);
    _userRepo.Setup(r => r.UpdateAsync(user)).ReturnsAsync(user);

    var op = Op(SyncOperationTypes.UserSettingsBudgetUpdate, "",
      Json(new { excludePendingFromBudget = true, reattributeOnAccountAssociationChange = true }));

    var response = await _service.ProcessBatchAsync(UserId, Batch(op));

    response.Results.Single().Status.Should().Be(SyncOperationStatus.Applied);
    user.ExcludePendingFromBudget.Should().BeTrue();
    _userRepo.Verify(r => r.UpdateAsync(user), Times.Once);
  }

  [Fact]
  public async Task ProcessBatchAsync_DuplicateKey_ReturnsDuplicateAndDoesNotReapply()
  {
    const string key = "dup-key-1";
    _db.SyncIdempotencyKeys.Add(new SyncIdempotencyKey
    {
      UserId = UserId,
      Key = key,
      OperationType = SyncOperationTypes.NotificationMarkRead,
      Status = SyncOperationStatus.Applied,
      ProcessedAt = DateTime.UtcNow,
    });
    await _db.SaveChangesAsync();

    var op = Op(SyncOperationTypes.NotificationMarkRead, "11", key: key);

    var response = await _service.ProcessBatchAsync(UserId, Batch(op));

    response.Results.Single().Status.Should().Be(SyncOperationStatus.Duplicate);
    _notifications.Verify(n => n.MarkAsReadAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task ProcessBatchAsync_ServerNewerThanBaseUpdatedAt_ReturnsConflictAndDoesNotApply()
  {
    // Seed a goal whose server UpdatedAt is well ahead of the client's baseUpdatedAt.
    _db.SavingsGoals.Add(new SavingsGoal
    {
      Id = 3,
      UserId = UserId,
      Name = "Trip",
      TargetAmount = 500,
      AllocationPercentage = 5,
      UpdatedAt = DateTime.UtcNow,
    });
    await _db.SaveChangesAsync();

    var op = Op(SyncOperationTypes.SavingsGoalUpdate, "3",
      Json(new { name = "Trip", description = "", targetAmount = 500m, allocationPercentage = 5m }),
      baseUpdatedAt: DateTime.UtcNow.AddMinutes(-10));

    var response = await _service.ProcessBatchAsync(UserId, Batch(op));

    response.Results.Single().Status.Should().Be(SyncOperationStatus.Conflict);
    _savings.Verify(s => s.UpdateSavingsGoalAsync(It.IsAny<int>(), It.IsAny<UpdateSavingsGoalRequest>(), It.IsAny<int>()), Times.Never);
    var row = await _db.SyncIdempotencyKeys.SingleAsync();
    row.Status.Should().Be(SyncOperationStatus.Conflict);
  }

  [Fact]
  public async Task ProcessBatchAsync_BaseUpdatedAtNewerThanServer_AppliesLastWriteWins()
  {
    _db.SavingsGoals.Add(new SavingsGoal
    {
      Id = 3,
      UserId = UserId,
      Name = "Trip",
      TargetAmount = 500,
      AllocationPercentage = 5,
      UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
    });
    await _db.SaveChangesAsync();
    _auth.Setup(a => a.CanAccessGoalAsync(UserId, 3, ResourcePermission.Edit))
      .ReturnsAsync(AuthorizationResult.Allowed());
    _savings.Setup(s => s.UpdateSavingsGoalAsync(3, It.IsAny<UpdateSavingsGoalRequest>(), UserId))
      .ReturnsAsync(new SavingsGoalDto { Id = 3 });

    var op = Op(SyncOperationTypes.SavingsGoalUpdate, "3",
      Json(new { name = "Trip", description = "", targetAmount = 500m, allocationPercentage = 5m }),
      baseUpdatedAt: DateTime.UtcNow);

    var response = await _service.ProcessBatchAsync(UserId, Batch(op));

    response.Results.Single().Status.Should().Be(SyncOperationStatus.Applied);
  }

  [Fact]
  public async Task ProcessBatchAsync_UnknownOperationType_ReturnsFailed()
  {
    var response = await _service.ProcessBatchAsync(
      UserId, Batch(Op("not_a_real_type", "1")));

    var result = response.Results.Single();
    result.Status.Should().Be(SyncOperationStatus.Failed);
    result.Error.Should().Be("unknown operation type");
  }

  [Fact]
  public async Task ProcessBatchAsync_MalformedPayload_ReturnsFailed()
  {
    // allocatedAmount is a string that can't bind to decimal → JsonException in the service.
    var op = Op(SyncOperationTypes.BudgetCategoryUpdate, "20",
      Json(new { allocatedAmount = "not-a-number" }));

    var response = await _service.ProcessBatchAsync(UserId, Batch(op));

    var result = response.Results.Single();
    result.Status.Should().Be(SyncOperationStatus.Failed);
    result.Error.Should().Be("malformed payload");
    _budget.Verify(b => b.UpdateBudgetCategoryAsync(It.IsAny<int>(), It.IsAny<UpdateBudgetCategoryRequest>()), Times.Never);
  }

  [Fact]
  public async Task ProcessBatchAsync_OneFailingOpDoesNotAbortRest_ProcessesAllInOrder()
  {
    _notifications.Setup(n => n.MarkAsReadAsync(UserId, 11)).ReturnsAsync(true);

    var failing = Op("not_a_real_type", "1");
    var good = Op(SyncOperationTypes.NotificationMarkRead, "11");

    var response = await _service.ProcessBatchAsync(UserId, Batch(failing, good));

    response.Results.Should().HaveCount(2);
    response.Results[0].Status.Should().Be(SyncOperationStatus.Failed);
    response.Results[1].Status.Should().Be(SyncOperationStatus.Applied);
    _notifications.Verify(n => n.MarkAsReadAsync(UserId, 11), Times.Once);
  }

  [Fact]
  public async Task ProcessBatchAsync_ResultsMatchSubmittedKeysAndOrder()
  {
    _notifications.Setup(n => n.MarkAsReadAsync(UserId, It.IsAny<int>())).ReturnsAsync(true);
    var op1 = Op(SyncOperationTypes.NotificationMarkRead, "1", key: "k1");
    var op2 = Op(SyncOperationTypes.NotificationMarkRead, "2", key: "k2");

    var response = await _service.ProcessBatchAsync(UserId, Batch(op1, op2));

    response.Results.Select(r => r.IdempotencyKey).Should().ContainInOrder("k1", "k2");
  }
}
