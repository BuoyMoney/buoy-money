using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs.CategorizationRule;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Tests for categorization-rule correctness against the unified Categories table
/// (custom categories are Id >= 1000 and have NO legacy UserCategories row):
///   - custom-category rules validate against the unified table (not UserCategories)
///   - family-shared rules are honored by the bulk apply paths
///   - rules orphaned to a null category never force a (mis)categorization
/// </summary>
public class CategorizationRuleServiceTests
{
    private const int UserId = 2;
    private const int FamilyId = 2;
    private const int AdminUserId = 99;
    private const int CustomCategoryId = 1000;
    private const int GroceriesId = 20;

    private static BuoyAIDbContext NewContext() =>
        new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static CategorizationRuleService CreateService(
        BuoyAIDbContext context,
        out Mock<IUserRepository> userRepo,
        out Mock<ICategoryRepository> categoryRepo,
        int? userFamilyId = FamilyId)
    {
        userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByIdAsync(UserId))
            .ReturnsAsync(new User { Id = UserId, FamilyId = userFamilyId, Email = "u@x.com" });

        categoryRepo = new Mock<ICategoryRepository>();

        return new CategorizationRuleService(
            context,
            userRepo.Object,
            categoryRepo.Object,
            new Mock<ILogger<CategorizationRuleService>>().Object);
    }

    private static Transaction Txn(int id, string merchant, decimal amount = -50m) => new Transaction
    {
        Id = id,
        MerchantName = merchant,
        Description = merchant,
        Amount = amount,
        Date = DateTime.UtcNow
    };

    // ---------------- Finding 2: custom-category rule validation ----------------

    [Fact]
    public async Task CreateRuleAsync_WithUnifiedCustomCategory_Succeeds()
    {
        using var ctx = NewContext();
        var service = CreateService(ctx, out _, out var categoryRepo);

        // The custom category exists in the unified Categories table and is accessible.
        categoryRepo.Setup(r => r.ExistsAndAccessibleAsync(CustomCategoryId, UserId, FamilyId))
            .ReturnsAsync(true);

        var request = new CreateCategorizationRuleRequest
        {
            Name = "PetSmart -> Pet Expenses",
            MerchantContains = "petsmart",
            AssignCategoryId = CustomCategoryId,
            IsActive = true
        };

        var dto = await service.CreateRuleAsync(UserId, request);

        dto.AssignCategoryId.Should().Be(CustomCategoryId);
        (await ctx.CategorizationRules.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateRuleAsync_WithInaccessibleCustomCategory_Throws()
    {
        using var ctx = NewContext();
        var service = CreateService(ctx, out _, out var categoryRepo);

        categoryRepo.Setup(r => r.ExistsAndAccessibleAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>()))
            .ReturnsAsync(false);

        var request = new CreateCategorizationRuleRequest
        {
            Name = "bad",
            MerchantContains = "petsmart",
            AssignCategoryId = 5000,
            IsActive = true
        };

        var act = () => service.CreateRuleAsync(UserId, request);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateRuleAsync_WithSystemCategory_DoesNotHitUnifiedRepo()
    {
        using var ctx = NewContext();
        var service = CreateService(ctx, out _, out var categoryRepo);

        var request = new CreateCategorizationRuleRequest
        {
            Name = "Costco -> Groceries",
            MerchantContains = "costco",
            AssignCategoryId = GroceriesId, // system (enum-backed)
            IsActive = true
        };

        var dto = await service.CreateRuleAsync(UserId, request);

        dto.AssignCategoryId.Should().Be(GroceriesId);
        categoryRepo.Verify(r => r.ExistsAndAccessibleAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>()), Times.Never);
    }

    // ---------------- Finding 4: family-shared rules in bulk apply ----------------

    [Fact]
    public async Task ApplyRulesToTransactionsAsync_HonorsFamilySharedRule()
    {
        using var ctx = NewContext();
        // A shared rule created by the family admin (different UserId), FamilyId set.
        ctx.CategorizationRules.Add(new CategorizationRule
        {
            Id = 1,
            UserId = AdminUserId,
            FamilyId = FamilyId,
            Name = "Costco -> Groceries",
            MerchantContains = "costco",
            AssignCategoryId = GroceriesId,
            IsActive = true,
            Priority = 0
        });
        await ctx.SaveChangesAsync();

        var service = CreateService(ctx, out _, out _);

        var txn = Txn(10, "COSTCO WHOLESALE");
        var applied = await service.ApplyRulesToTransactionsAsync(UserId, new[] { txn });

        applied.Should().Be(1);
        txn.CategoryId.Should().Be(GroceriesId);
        txn.IsUserCategorized.Should().BeTrue();
    }

    // ---------------- Finding 3: orphaned (null-category) rules ----------------

    [Fact]
    public async Task ApplyRulesToTransactionsAsync_SkipsRuleWithNullCategory()
    {
        using var ctx = NewContext();
        // Rule whose category was deleted: AssignCategoryId is null but rule stays active.
        ctx.CategorizationRules.Add(new CategorizationRule
        {
            Id = 1,
            UserId = UserId,
            Name = "orphaned",
            MerchantContains = "costco",
            AssignCategoryId = null,
            IsActive = true,
            Priority = 0
        });
        await ctx.SaveChangesAsync();

        var service = CreateService(ctx, out _, out _);

        var txn = Txn(10, "COSTCO");
        txn.NeedsReview = true;
        txn.CategoryId = null;

        var applied = await service.ApplyRulesToTransactionsAsync(UserId, new[] { txn });

        applied.Should().Be(0);
        txn.CategoryId.Should().BeNull();          // NOT forced to Uncategorized
        txn.NeedsReview.Should().BeTrue();          // still surfaced for review
    }

    [Fact]
    public async Task DeactivateRulesForCategoryAsync_DeactivatesReferencingRules()
    {
        using var ctx = NewContext();
        ctx.CategorizationRules.AddRange(
            new CategorizationRule { Id = 1, UserId = UserId, Name = "a", MerchantContains = "x", AssignCategoryId = CustomCategoryId, IsActive = true },
            new CategorizationRule { Id = 2, UserId = UserId, Name = "b", MerchantContains = "y", AssignCategoryId = GroceriesId, IsActive = true });
        await ctx.SaveChangesAsync();

        var service = CreateService(ctx, out _, out _);

        var count = await service.DeactivateRulesForCategoryAsync(CustomCategoryId);

        count.Should().Be(1);
        (await ctx.CategorizationRules.FindAsync(1))!.IsActive.Should().BeFalse();
        (await ctx.CategorizationRules.FindAsync(2))!.IsActive.Should().BeTrue();
    }
}
