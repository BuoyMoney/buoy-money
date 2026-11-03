using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The single-transaction review actions used to fetch by id alone and pass userId only to
/// their log line, so any authenticated user could accept, recategorise, or snooze any
/// transaction by guessing an id. These pin the guard shut.
/// </summary>
public class AICategorizationOwnershipTests
{
    private const int OwnerId = 2;
    private const int StrangerId = 99;
    private const int FamilyMemberId = 7;
    private const int TransactionId = 500;
    private const int FamilyId = 42;

    private static Mock<ITransactionRepository> _txnRepo = null!;

    private static AICategorizationService CreateService(int? ownerFamilyId = null)
    {
        var transaction = new Transaction
        {
            Id = TransactionId,
            Date = DateTime.UtcNow.AddDays(-3),
            NeedsReview = true,
            IsUserCategorized = false,
            MerchantName = "Merchant",
            Amount = -10m,
            AISuggestedCategoryId = 5,
            Account = new Account { Id = 1, UserId = OwnerId, Name = "Checking" },
        };

        _txnRepo = new Mock<ITransactionRepository>();
        _txnRepo.Setup(r => r.GetByIdAsync(TransactionId)).ReturnsAsync(transaction);
        _txnRepo.Setup(r => r.UpdateAsync(It.IsAny<Transaction>()))
            .ReturnsAsync((Transaction t) => t);

        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByIdAsync(OwnerId)).ReturnsAsync(
            new User { Id = OwnerId, FamilyId = ownerFamilyId, Email = "owner@x.com" });
        userRepo.Setup(r => r.GetByIdAsync(StrangerId)).ReturnsAsync(
            new User { Id = StrangerId, FamilyId = null, Email = "stranger@x.com" });
        userRepo.Setup(r => r.GetByIdAsync(FamilyMemberId)).ReturnsAsync(
            new User { Id = FamilyMemberId, FamilyId = FamilyId, Email = "member@x.com" });

        var config = new ConfigurationBuilder().Build();
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        return new AICategorizationService(
            _txnRepo.Object,
            userRepo.Object,
            new Mock<IEntitlementService>().Object,
            new Mock<ICategorizationRuleService>().Object,
            new Mock<ITransactionEmbeddingService>().Object,
            new Mock<ICategoryRepository>().Object,
            new BuoyAIDbContext(options),
            config,
            new Mock<ILogger<AICategorizationService>>().Object,
            new AIModelConfiguration(config),
            TestGeminiUsage.Create());
    }

    [Fact]
    public async Task SkipTransactionReviewAsync_ForAnotherUsersTransaction_Throws()
    {
        var service = CreateService();

        var act = async () => await service.SkipTransactionReviewAsync(TransactionId, StrangerId);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        _txnRepo.Verify(r => r.UpdateAsync(It.IsAny<Transaction>()), Times.Never);
    }

    [Fact]
    public async Task AcceptAISuggestionAsync_ForAnotherUsersTransaction_Throws()
    {
        var service = CreateService();

        var act = async () => await service.AcceptAISuggestionAsync(TransactionId, StrangerId);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        _txnRepo.Verify(r => r.UpdateAsync(It.IsAny<Transaction>()), Times.Never);
    }

    [Fact]
    public async Task RejectAISuggestionAsync_ForAnotherUsersTransaction_Throws()
    {
        var service = CreateService();

        var act = async () => await service.RejectAISuggestionAsync(TransactionId, StrangerId, 12);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        _txnRepo.Verify(r => r.UpdateAsync(It.IsAny<Transaction>()), Times.Never);
    }

    [Fact]
    public async Task Denial_LooksIdenticalToNotFound_SoIdsCannotBeProbed()
    {
        var service = CreateService();
        _txnRepo.Setup(r => r.GetByIdAsync(4242)).ReturnsAsync((Transaction?)null);

        var forbidden = await Record.ExceptionAsync(
            () => service.SkipTransactionReviewAsync(TransactionId, StrangerId));
        var missing = await Record.ExceptionAsync(
            () => service.SkipTransactionReviewAsync(4242, StrangerId));

        // Same type AND same message: a different response would confirm which ids exist.
        forbidden.Should().BeOfType<KeyNotFoundException>();
        missing.Should().BeOfType<KeyNotFoundException>();
        forbidden!.Message.Should().Be($"Transaction {TransactionId} not found");
        missing!.Message.Should().Be("Transaction 4242 not found");
    }

    [Fact]
    public async Task SkipTransactionReviewAsync_ForOwnTransaction_Succeeds()
    {
        var service = CreateService();

        var result = await service.SkipTransactionReviewAsync(TransactionId, OwnerId);

        result.Should().NotBeNull();
        _txnRepo.Verify(r => r.UpdateAsync(It.IsAny<Transaction>()), Times.Once);
    }

    [Fact]
    public async Task SkipTransactionReviewAsync_ForAFamilyMembersTransaction_Succeeds()
    {
        // Family sharing is the whole point of GetByUserOrFamilyAsync elsewhere; the guard
        // must not break it by scoping to the owner alone.
        var service = CreateService(ownerFamilyId: FamilyId);

        var result = await service.SkipTransactionReviewAsync(TransactionId, FamilyMemberId);

        result.Should().NotBeNull();
        _txnRepo.Verify(r => r.UpdateAsync(It.IsAny<Transaction>()), Times.Once);
    }

    [Fact]
    public async Task AcceptAISuggestionAsync_ForAFamilyMembersTransaction_Succeeds()
    {
        var service = CreateService(ownerFamilyId: FamilyId);

        var result = await service.AcceptAISuggestionAsync(TransactionId, FamilyMemberId);

        result.Should().NotBeNull();
    }
}
