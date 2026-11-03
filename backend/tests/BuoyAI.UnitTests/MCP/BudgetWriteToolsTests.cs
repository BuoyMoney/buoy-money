using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.MCP.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.MCP;

public class BudgetWriteToolsTests
{
    [Fact]
    public async Task UpdateBudgetAmount_PreservesRolloverAndCountUpConfig()
    {
        var budgetService = new Mock<IBudgetService>();
        var category = new BudgetCategoryDto
        {
            Id = 5,
            CategoryName = "Groceries",
            AllocatedAmount = 400,
            AllowRollover = true,
            MaxRolloverMultiplier = 2m,
            RolloverOverflowSavingsGoalId = 7,
            CountUpRolloverMode = CountUpRolloverMode.TransferToSavings,
            SavingsGoalId = 9,
        };
        budgetService.Setup(s => s.GetAccessibleBudgetsAsync(1))
            .ReturnsAsync(new List<BudgetDto>
            {
                new() { Id = 1, Name = "Main", Categories = new List<BudgetCategoryDto> { category } },
            });

        UpdateBudgetCategoryRequest? captured = null;
        budgetService.Setup(s => s.UpdateBudgetCategoryAsync(5, It.IsAny<UpdateBudgetCategoryRequest>()))
            .Callback((int _, UpdateBudgetCategoryRequest r) => captured = r)
            .ReturnsAsync(new BudgetCategoryDto { Id = 5, CategoryName = "Groceries", AllocatedAmount = 600 });

        var tools = new BudgetWriteTools(
            budgetService.Object, Mock.Of<IUserRepository>(), Mock.Of<ILogger<BudgetWriteTools>>());

        // Change only the amount (the tap/confirmation path).
        await tools.UpdateBudgetAmountAsync(userId: 1, categoryId: 5, newAmount: 600, applyToFuture: false, confirmed: true);

        captured.Should().NotBeNull();
        captured!.AllocatedAmount.Should().Be(600);
        // An amount-only change must NOT reset the category's rollover / count-up configuration.
        captured.AllowRollover.Should().BeTrue();
        captured.MaxRolloverMultiplier.Should().Be(2m);
        captured.RolloverOverflowSavingsGoalId.Should().Be(7);
        captured.CountUpRolloverMode.Should().Be(CountUpRolloverMode.TransferToSavings);
        captured.SavingsGoalId.Should().Be(9);
    }

    [Fact]
    public async Task UpdateBudgetAmount_NotConfirmed_ReturnsPreviewWithoutWriting()
    {
        var budgetService = new Mock<IBudgetService>();
        budgetService.Setup(s => s.GetAccessibleBudgetsAsync(1))
            .ReturnsAsync(new List<BudgetDto>
            {
                new()
                {
                    Id = 1, Name = "Main",
                    Categories = new List<BudgetCategoryDto>
                    {
                        new() { Id = 5, CategoryName = "Groceries", AllocatedAmount = 400 },
                    },
                },
            });

        var tools = new BudgetWriteTools(
            budgetService.Object, Mock.Of<IUserRepository>(), Mock.Of<ILogger<BudgetWriteTools>>());

        var result = await tools.UpdateBudgetAmountAsync(userId: 1, categoryId: 5, newAmount: 600, confirmed: false);

        result.Should().Contain("RequiresConfirmation");
        budgetService.Verify(s => s.UpdateBudgetCategoryAsync(It.IsAny<int>(), It.IsAny<UpdateBudgetCategoryRequest>()), Times.Never);
    }
}
