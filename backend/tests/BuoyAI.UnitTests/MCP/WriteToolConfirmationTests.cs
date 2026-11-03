using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.MCP.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.MCP;

/// <summary>
/// "Preview first, then the user confirms" used to live only in the system prompt: a model that
/// called a write tool with confirmed=true wrote immediately. ExecuteToolAsync (the path every
/// model-sourced call takes) now forces the preview; only ExecuteConfirmedToolAsync (a user's
/// Confirm tap, or an insight-action tap) writes.
/// </summary>
public class WriteToolConfirmationTests
{
  private const int UserId = 1;
  private readonly Mock<IBudgetService> _budgets = new();
  private readonly McpToolExecutor _executor;

  public WriteToolConfirmationTests()
  {
    _budgets.Setup(s => s.GetAccessibleBudgetsAsync(UserId))
      .ReturnsAsync(new List<BudgetDto>
      {
        new()
        {
          Id = 1, Name = "Main",
          Categories = new List<BudgetCategoryDto> { new() { Id = 5, CategoryName = "Dining Out", AllocatedAmount = 650 } },
        },
      });
    _budgets.Setup(s => s.UpdateBudgetCategoryAsync(5, It.IsAny<UpdateBudgetCategoryRequest>()))
      .ReturnsAsync(new BudgetCategoryDto { Id = 5, CategoryName = "Dining Out", AllocatedAmount = 400 });

    var family = new Mock<IFamilyService>();
    family.Setup(f => f.CanUserModifyFinancialDataAsync(UserId)).ReturnsAsync(true);

    var services = new ServiceCollection();
    services.AddSingleton(_budgets.Object);
    services.AddSingleton(Mock.Of<IUserRepository>());
    services.AddSingleton(family.Object);
    services.AddSingleton(Mock.Of<ILogger<BudgetWriteTools>>());
    _executor = new McpToolExecutor(services.BuildServiceProvider(), Mock.Of<ILogger<McpToolExecutor>>());
  }

  private static Dictionary<string, object> Args(params (string Key, object Value)[] extra)
  {
    var args = new Dictionary<string, object> { ["categoryId"] = 5, ["newAmount"] = 400m };
    foreach (var (k, v) in extra)
    {
      args[k] = v;
    }
    return args;
  }

  [Fact]
  public async Task ModelSentConfirmedTrue_ReturnsThePreviewAndDoesNotWrite()
  {
    var result = await _executor.ExecuteToolAsync(UserId, "update_budget_amount", Args(("confirmed", true)));

    result.Should().Contain("RequiresConfirmation");
    _budgets.Verify(s => s.UpdateBudgetCategoryAsync(It.IsAny<int>(), It.IsAny<UpdateBudgetCategoryRequest>()), Times.Never);
  }

  [Fact]
  public async Task ModelSentConfirmedWithOtherCasing_StillDoesNotWrite()
  {
    var result = await _executor.ExecuteToolAsync(UserId, "update_budget_amount", Args(("Confirmed", "true")));

    result.Should().Contain("RequiresConfirmation");
    _budgets.Verify(s => s.UpdateBudgetCategoryAsync(It.IsAny<int>(), It.IsAny<UpdateBudgetCategoryRequest>()), Times.Never);
  }

  [Fact]
  public async Task UserConfirmedExecution_Writes()
  {
    var result = await _executor.ExecuteConfirmedToolAsync(UserId, "update_budget_amount", Args());

    result.Should().NotContain("RequiresConfirmation");
    _budgets.Verify(s => s.UpdateBudgetCategoryAsync(5, It.Is<UpdateBudgetCategoryRequest>(r => r.AllocatedAmount == 400m)), Times.Once);
  }
}
