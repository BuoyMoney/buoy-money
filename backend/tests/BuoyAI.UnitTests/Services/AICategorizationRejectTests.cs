using System;
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
/// Tests that RejectAISuggestionAsync validates the target category before assigning
/// it (finding: an undefined enum value 500'd on the FK, and custom categories were
/// unreachable through the enum-typed request).
/// </summary>
public class AICategorizationRejectTests
{
    private const int UserId = 2;
    private const int TransactionId = 10;
    private const int CustomCategoryId = 1000;

    private static (AICategorizationService service,
                    Mock<ITransactionRepository> txnRepo,
                    Mock<ICategoryRepository> catRepo) CreateService()
    {
        var txnRepo = new Mock<ITransactionRepository>();
        txnRepo.Setup(r => r.GetByIdAsync(TransactionId))
            // Account is a required FK and GetByIdAsync eager-loads it, so a real transaction
            // always has one. It has to be present here too or the ownership guard on the
            // review actions (correctly) refuses to act on an ownerless transaction.
            .ReturnsAsync(new Transaction
            {
                Id = TransactionId,
                Amount = -25m,
                MerchantName = "PetSmart",
                Account = new Account { Id = 1, UserId = UserId, Name = "Checking" },
            });
        txnRepo.Setup(r => r.UpdateAsync(It.IsAny<Transaction>()))
            .ReturnsAsync((Transaction t) => t);

        var catRepo = new Mock<ICategoryRepository>();
        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByIdAsync(UserId))
            .ReturnsAsync(new User { Id = UserId, FamilyId = null, Email = "u@x.com" });

        var config = new ConfigurationBuilder().Build();
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        var service = new AICategorizationService(
            txnRepo.Object,
            userRepo.Object,
            new Mock<IEntitlementService>().Object,
            new Mock<ICategorizationRuleService>().Object,
            new Mock<ITransactionEmbeddingService>().Object,
            catRepo.Object,
            new BuoyAIDbContext(options),
            config,
            new Mock<ILogger<AICategorizationService>>().Object,
            new AIModelConfiguration(config),
            TestGeminiUsage.Create());

        return (service, txnRepo, catRepo);
    }

    [Theory]
    [InlineData(0)]   // omitted enum default -> not defined
    [InlineData(50)]  // undefined value
    public async Task RejectAISuggestionAsync_UndefinedSystemCategory_ThrowsArgumentException(int categoryId)
    {
        var (service, txnRepo, _) = CreateService();

        var act = () => service.RejectAISuggestionAsync(TransactionId, UserId, categoryId);

        await act.Should().ThrowAsync<ArgumentException>();
        txnRepo.Verify(r => r.UpdateAsync(It.IsAny<Transaction>()), Times.Never);
    }

    [Fact]
    public async Task RejectAISuggestionAsync_InaccessibleCustomCategory_ThrowsArgumentException()
    {
        var (service, txnRepo, catRepo) = CreateService();
        catRepo.Setup(r => r.ExistsAndAccessibleAsync(CustomCategoryId, UserId, It.IsAny<int?>()))
            .ReturnsAsync(false);

        var act = () => service.RejectAISuggestionAsync(TransactionId, UserId, CustomCategoryId);

        await act.Should().ThrowAsync<ArgumentException>();
        txnRepo.Verify(r => r.UpdateAsync(It.IsAny<Transaction>()), Times.Never);
    }

    [Fact]
    public async Task RejectAISuggestionAsync_ValidCustomCategory_AssignsUnifiedId()
    {
        var (service, txnRepo, catRepo) = CreateService();
        catRepo.Setup(r => r.ExistsAndAccessibleAsync(CustomCategoryId, UserId, It.IsAny<int?>()))
            .ReturnsAsync(true);
        catRepo.Setup(r => r.GetByIdAsync(CustomCategoryId))
            .ReturnsAsync(new Category { Id = CustomCategoryId, Name = "Pet Expenses" });

        var result = await service.RejectAISuggestionAsync(TransactionId, UserId, CustomCategoryId);

        result.CategoryId.Should().Be(CustomCategoryId);
        txnRepo.Verify(r => r.UpdateAsync(It.Is<Transaction>(t =>
            t.CategoryId == CustomCategoryId && t.IsUserCategorized && !t.NeedsReview)), Times.Once);
    }

    [Fact]
    public async Task RejectAISuggestionAsync_ValidSystemCategory_Assigns()
    {
        var (service, txnRepo, catRepo) = CreateService();

        var result = await service.RejectAISuggestionAsync(TransactionId, UserId, (int)Core.Enums.TransactionCategory.Groceries);

        result.CategoryId.Should().Be((int)Core.Enums.TransactionCategory.Groceries);
        catRepo.Verify(r => r.ExistsAndAccessibleAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>()), Times.Never);
    }
}
