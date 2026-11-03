using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class TransferDetectionServiceTests
{
    private readonly Mock<ITransactionRepository> _txRepo = new();
    private readonly Mock<IAccountRepository> _accountRepo = new();
    private readonly Mock<IUserRepository> _userRepo = new();
    private readonly Mock<ITransactionService> _txService = new();
    private readonly Mock<ISavingsService> _savingsService = new();

    // The InMemory provider doesn't support real transactions; ignore the warning so the
    // atomicity wrapper (BeginTransactionAsync) is a no-op in unit tests. The all-or-nothing
    // contract is still exercised at the observable level (partial failure => returns 0).
    private static BuoyAIDbContext NewContext() => new(
        new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private TransferDetectionService CreateService(BuoyAIDbContext? ctx = null) => new(
        _txRepo.Object, _accountRepo.Object, _userRepo.Object, _txService.Object,
        _savingsService.Object, ctx ?? NewContext(),
        Mock.Of<ILogger<TransferDetectionService>>());

    private static Transaction Tx(int id, int accountId, decimal amount, TransactionType type) => new()
    {
        Id = id,
        AccountId = accountId,
        Amount = amount,
        Type = type,
        Date = new DateTime(2026, 7, 1),
        CategoryId = (int)TransactionCategory.Uncategorized,
        IsUserCategorized = false,
        IsSplit = false,
        MerchantName = "Transfer",
    };

    [Fact]
    public async Task GetSuspectedTransfers_ProjectsPairAndResolvesAccountNames()
    {
        _userRepo.Setup(u => u.GetByIdAsync(1)).ReturnsAsync(new User { Id = 1 });
        _txRepo.Setup(r => r.GetByUserOrFamilyAsync(1, null, It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(new List<Transaction>
            {
                Tx(1, 10, -2000, TransactionType.Debit),
                Tx(2, 20, 2000, TransactionType.Credit),
            });
        _accountRepo.Setup(a => a.GetByUserOrFamilyAsync(1, null)).ReturnsAsync(new List<Account>
        {
            new() { Id = 10, Name = "Checking" },
            new() { Id = 20, Name = "Savings" },
        });

        var result = await CreateService().GetSuspectedTransfersAsync(1);

        result.Should().ContainSingle();
        result[0].Confidence.Should().Be("medium");
        result[0].Sides.Should().HaveCount(2);
        result[0].Sides.Select(s => s.AccountName).Should().Contain(new[] { "Checking", "Savings" });
    }

    [Fact]
    public async Task GetSuspectedTransfers_ExcludesLegDismissedAsNotATransfer()
    {
        _userRepo.Setup(u => u.GetByIdAsync(1)).ReturnsAsync(new User { Id = 1 });
        var debit = Tx(1, 10, -2000, TransactionType.Debit);
        // The debit was dismissed ("not a transfer") — the pair must no longer be suggested.
        debit.TransferReviewedAt = DateTime.UtcNow;
        _txRepo.Setup(r => r.GetByUserOrFamilyAsync(1, null, It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(new List<Transaction> { debit, Tx(2, 20, 2000, TransactionType.Credit) });
        _accountRepo.Setup(a => a.GetByUserOrFamilyAsync(1, null)).ReturnsAsync(new List<Account>
        {
            new() { Id = 10, Name = "Checking" },
            new() { Id = 20, Name = "Savings" },
        });

        var result = await CreateService().GetSuspectedTransfersAsync(1);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmTransfers_RecategorizesEachAsTransfer()
    {
        _txService.Setup(s => s.GetTransactionByIdAsync(It.IsAny<int>(), 1))
            .ReturnsAsync((int id, int _) => new TransactionDto { Id = id });
        var updatedCategories = new List<int?>();
        _txService.Setup(s => s.UpdateTransactionCategoryAsync(It.IsAny<int>(), It.IsAny<UpdateTransactionCategoryRequest>(), 1))
            .Callback((int _, UpdateTransactionCategoryRequest r, int _) => updatedCategories.Add(r.CategoryId))
            .ReturnsAsync(new TransactionDto());

        var count = await CreateService().ConfirmTransfersAsync(1, new[] { 1, 2 });

        count.Should().Be(2);
        updatedCategories.Should().AllBeEquivalentTo((int)TransactionCategory.Transfer);
    }

    [Fact]
    public async Task ConfirmTransfers_PartialFailure_IsAllOrNothing_ReturnsZero()
    {
        _txService.Setup(s => s.GetTransactionByIdAsync(It.IsAny<int>(), 1))
            .ReturnsAsync((int id, int _) => new TransactionDto { Id = id });
        // Leg 1 succeeds, leg 2 throws (e.g. concurrent edit / transient DB error).
        _txService.Setup(s => s.UpdateTransactionCategoryAsync(1, It.IsAny<UpdateTransactionCategoryRequest>(), 1))
            .ReturnsAsync(new TransactionDto());
        _txService.Setup(s => s.UpdateTransactionCategoryAsync(2, It.IsAny<UpdateTransactionCategoryRequest>(), 1))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var count = await CreateService().ConfirmTransfersAsync(1, new[] { 1, 2 });

        // Not 1 — a partially-confirmed pair is worse than none, so the whole batch reports failure.
        count.Should().Be(0);
    }

    [Fact]
    public async Task ConfirmTransfers_MissingLeg_AbortsWholePair()
    {
        // Leg 1 is fetchable, leg 2 vanished between suggestion and confirm.
        _txService.Setup(s => s.GetTransactionByIdAsync(1, 1)).ReturnsAsync(new TransactionDto { Id = 1 });
        _txService.Setup(s => s.GetTransactionByIdAsync(2, 1)).ReturnsAsync((TransactionDto?)null);
        _txService.Setup(s => s.UpdateTransactionCategoryAsync(It.IsAny<int>(), It.IsAny<UpdateTransactionCategoryRequest>(), 1))
            .ReturnsAsync(new TransactionDto());

        var count = await CreateService().ConfirmTransfersAsync(1, new[] { 1, 2 });

        count.Should().Be(0);
    }

    [Fact]
    public async Task ConfirmTransfers_EarmarkedExpenseLeg_ReversesWithdrawalBeforeUnlinking()
    {
        // An earmarked $200 debit (withdrawal from goal 7) is confirmed as a transfer.
        _txService.Setup(s => s.GetTransactionByIdAsync(1, 1)).ReturnsAsync(new TransactionDto
        {
            Id = 1,
            Amount = -200m,
            Type = TransactionType.Debit,
            CategoryId = (int)TransactionCategory.Shopping,
            SavingsGoalId = 7,
        });
        _txService.Setup(s => s.UpdateTransactionCategoryAsync(1, It.IsAny<UpdateTransactionCategoryRequest>(), 1))
            .ReturnsAsync(new TransactionDto());

        var count = await CreateService().ConfirmTransfersAsync(1, new[] { 1 });

        count.Should().Be(1);
        // The goal movement must be REVERSED (not just dropped) then the link cleared.
        _savingsService.Verify(s => s.UndoWithdrawalAsync(7, 1), Times.Once);
        _savingsService.Verify(s => s.UndoDepositAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _txService.Verify(s => s.LinkToSavingsGoalAsync(1, null, 1), Times.Once);
    }

    [Fact]
    public async Task ConfirmTransfers_EarmarkedIncomeLeg_ReversesDepositBeforeUnlinking()
    {
        _txService.Setup(s => s.GetTransactionByIdAsync(1, 1)).ReturnsAsync(new TransactionDto
        {
            Id = 1,
            Amount = 200m,
            Type = TransactionType.Credit,
            CategoryId = (int)TransactionCategory.Uncategorized,
            SavingsGoalId = 9,
        });
        _txService.Setup(s => s.UpdateTransactionCategoryAsync(1, It.IsAny<UpdateTransactionCategoryRequest>(), 1))
            .ReturnsAsync(new TransactionDto());

        var count = await CreateService().ConfirmTransfersAsync(1, new[] { 1 });

        count.Should().Be(1);
        _savingsService.Verify(s => s.UndoDepositAsync(9, 1), Times.Once);
        _savingsService.Verify(s => s.UndoWithdrawalAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _txService.Verify(s => s.LinkToSavingsGoalAsync(1, null, 1), Times.Once);
    }

    [Fact]
    public async Task DismissTransfers_MarksTransferReviewed_WithoutForcingCategorized()
    {
        var count = await CreateService().DismissTransfersAsync(1, new[] { 5 });

        count.Should().Be(1);
        // Dismiss sets the dedicated transfer-reviewed marker; it must NOT run the category-update
        // path (which would set IsUserCategorized and pull the row out of the review queue).
        _txService.Verify(s => s.MarkTransferReviewedAsync(5, 1), Times.Once);
        _txService.Verify(
            s => s.UpdateTransactionCategoryAsync(It.IsAny<int>(), It.IsAny<UpdateTransactionCategoryRequest>(), It.IsAny<int>()),
            Times.Never);
    }
}
