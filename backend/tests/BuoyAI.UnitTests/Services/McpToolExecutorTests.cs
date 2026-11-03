using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.MCP.Tools;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.UnitTests.Services;

public class McpToolExecutorTests : IDisposable
{
  private readonly Mock<ILogger<McpToolExecutor>> _mockLogger;
  private readonly Mock<IAccountRepository> _mockAccountRepository;
  private readonly Mock<ITransactionRepository> _mockTransactionRepository;
  private readonly Mock<IBudgetService> _mockBudgetService;
  private readonly Mock<ICountUpBudgetService> _mockCountUpBudgetService;
  private readonly Mock<ISavingsService> _mockSavingsService;
  private readonly Mock<IUserRepository> _mockUserRepository;
  private readonly Mock<ITransactionService> _mockTransactionService;
  private readonly Mock<IFamilyService> _mockFamilyService;
  private readonly Mock<IFamilyAuthorizationService> _mockFamilyAuthService;
  private readonly Mock<ILogger<FinancialReadTools>> _mockFinancialLogger;
  private readonly Mock<ILogger<BudgetReadTools>> _mockBudgetReadLogger;
  private readonly Mock<ILogger<BudgetWriteTools>> _mockBudgetWriteLogger;
  private readonly Mock<ILogger<SavingsReadTools>> _mockSavingsReadLogger;
  private readonly Mock<ILogger<SavingsWriteTools>> _mockSavingsWriteLogger;
  private readonly Mock<ILogger<UserPreferencesTools>> _mockUserPrefsLogger;
  private readonly Mock<ILogger<TransactionWriteTools>> _mockTransactionWriteLogger;
  private readonly IServiceProvider _serviceProvider;
  private readonly McpToolExecutor _executor;

  private const int TestUserId = 1;

  public McpToolExecutorTests()
  {
    _mockLogger = new Mock<ILogger<McpToolExecutor>>();
    _mockAccountRepository = new Mock<IAccountRepository>();
    _mockTransactionRepository = new Mock<ITransactionRepository>();
    _mockBudgetService = new Mock<IBudgetService>();
    _mockCountUpBudgetService = new Mock<ICountUpBudgetService>();
    _mockSavingsService = new Mock<ISavingsService>();
    _mockUserRepository = new Mock<IUserRepository>();
    _mockTransactionService = new Mock<ITransactionService>();
    _mockFamilyService = new Mock<IFamilyService>();
    _mockFamilyAuthService = new Mock<IFamilyAuthorizationService>();
    // Default: user is allowed to modify financial data (write-tool gate).
    _mockFamilyService.Setup(f => f.CanUserModifyFinancialDataAsync(It.IsAny<int>())).ReturnsAsync(true);
    _mockFinancialLogger = new Mock<ILogger<FinancialReadTools>>();
    _mockBudgetReadLogger = new Mock<ILogger<BudgetReadTools>>();
    _mockBudgetWriteLogger = new Mock<ILogger<BudgetWriteTools>>();
    _mockSavingsReadLogger = new Mock<ILogger<SavingsReadTools>>();
    _mockSavingsWriteLogger = new Mock<ILogger<SavingsWriteTools>>();
    _mockUserPrefsLogger = new Mock<ILogger<UserPreferencesTools>>();
    _mockTransactionWriteLogger = new Mock<ILogger<TransactionWriteTools>>();

    // Create a service collection with all the mocked dependencies
    var services = new ServiceCollection();
    services.AddSingleton(_mockAccountRepository.Object);
    services.AddSingleton(_mockTransactionRepository.Object);
    services.AddSingleton(_mockBudgetService.Object);
    services.AddSingleton(_mockCountUpBudgetService.Object);
    services.AddSingleton(_mockSavingsService.Object);
    services.AddSingleton(_mockUserRepository.Object);
    services.AddSingleton(_mockTransactionService.Object);
    services.AddSingleton(_mockFamilyService.Object);
    services.AddSingleton(_mockFamilyAuthService.Object);
    services.AddSingleton(_mockFinancialLogger.Object);
    services.AddSingleton(_mockBudgetReadLogger.Object);
    services.AddSingleton(_mockBudgetWriteLogger.Object);
    services.AddSingleton(_mockSavingsReadLogger.Object);
    services.AddSingleton(_mockSavingsWriteLogger.Object);
    services.AddSingleton(_mockUserPrefsLogger.Object);
    services.AddSingleton(_mockTransactionWriteLogger.Object);
    
    _serviceProvider = services.BuildServiceProvider();
    _executor = new McpToolExecutor(_serviceProvider, _mockLogger.Object);
  }

  public void Dispose()
  {
    GC.SuppressFinalize(this);
  }

  #region GetAvailableTools Tests

  [Fact]
  public void GetAvailableTools_ShouldReturnAllRegisteredTools()
  {
    // Act
    var tools = _executor.GetAvailableTools();

    // Assert
    tools.Should().NotBeEmpty();
    tools.Should().Contain(t => t.Name == "get_budgets");
    tools.Should().Contain(t => t.Name == "get_savings_goals");
  }

  [Fact]
  public void GetAvailableTools_ShouldIncludeDescriptions()
  {
    // Act
    var tools = _executor.GetAvailableTools();

    // Assert
    foreach (var tool in tools)
    {
      tool.Description.Should().NotBeNullOrEmpty();
    }
  }

  [Fact]
  public void GetAvailableTools_ShouldHaveCorrectReadOnlyFlag()
  {
    // Act
    var tools = _executor.GetAvailableTools();

    // Assert - Write tools should not be read-only
    var createBudgetTool = tools.FirstOrDefault(t => t.Name == "create_budget_category");
    createBudgetTool.Should().NotBeNull();
    createBudgetTool!.ReadOnly.Should().BeFalse();

    // Assert - Read tools should be read-only
    var getBudgetsTool = tools.FirstOrDefault(t => t.Name == "get_budgets");
    getBudgetsTool.Should().NotBeNull();
    getBudgetsTool!.ReadOnly.Should().BeTrue();
  }

  [Fact]
  public void GetGeminiFunctionDeclarations_ReadOnlyOnly_ExcludesWriteTools()
  {
    var readOnlyNames = _executor.GetGeminiFunctionDeclarations(readOnlyOnly: true)
      .Select(d => d.Name)
      .ToList();
    var writeNames = _executor.GetAvailableTools()
      .Where(t => !t.ReadOnly)
      .Select(t => t.Name)
      .ToList();

    readOnlyNames.Should().Contain("get_budgets");
    readOnlyNames.Should().NotContain("create_budget_category");
    // No write tool may leak into the read-only declaration set (agentic drill-down safety).
    readOnlyNames.Should().NotIntersectWith(writeNames);
  }

  [Fact]
  public void GetGeminiFunctionDeclarations_AllowList_ReturnsOnlyNamedTools()
  {
    var allowed = new HashSet<string> { "get_budgets", "get_transactions" };

    var names = _executor.GetGeminiFunctionDeclarations(readOnlyOnly: false, allowedToolNames: allowed)
      .Select(d => d.Name)
      .ToList();

    names.Should().OnlyContain(n => allowed.Contains(n));
    names.Should().Contain("get_budgets");
  }

  #endregion

  #region ExecuteToolAsync Tests

  [Fact]
  public async Task ExecuteToolAsync_WithGetBudgets_ShouldReturnBudgetData()
  {
    // Arrange
    var budgets = new List<BudgetDto>
    {
      new BudgetDto
      {
        Id = 1,
        Name = "Test Budget",
        TotalIncome = 1000m,
        TotalExpenses = 500m,
        Categories = new List<BudgetCategoryDto>()
      }
    };
    // The BudgetReadTools.GetBudgetsAsync calls GetAccessibleBudgetsAsync
    _mockBudgetService.Setup(s => s.GetAccessibleBudgetsAsync(TestUserId))
      .ReturnsAsync(budgets);

    // Act
    var result = await _executor.ExecuteToolAsync(TestUserId, "get_budgets", new Dictionary<string, object>());

    // Assert
    result.Should().NotBeNullOrEmpty();
    result.Should().Contain("Test Budget");
  }

  [Fact]
  public async Task ExecuteToolAsync_WithUnknownTool_ShouldReturnErrorMessage()
  {
    // Act
    var result = await _executor.ExecuteToolAsync(TestUserId, "unknown_tool", new Dictionary<string, object>());

    // Assert
    result.Should().Contain("Unknown tool");
  }

  #endregion

  #region GetToolsPrompt Tests

  [Fact]
  public void GetToolsPrompt_ShouldIncludeAllTools()
  {
    // Act
    var prompt = _executor.GetToolsPrompt();

    // Assert
    prompt.Should().Contain("get_budgets");
    prompt.Should().Contain("get_savings_goals");
  }

  [Fact]
  public void GetToolsPrompt_ShouldIncludeToolCallingFormat()
  {
    // Act
    var prompt = _executor.GetToolsPrompt();

    // Assert
    prompt.Should().Contain("tool_call");
    // Tool prompts should include tool definitions
    prompt.Length.Should().BeGreaterThan(500); // Should be substantial
  }

  #endregion

  #region Savings Tool Tests

  [Fact]
  public async Task ExecuteToolAsync_WithGetSavingsGoals_ShouldReturnGoals()
  {
    // Arrange
    var goals = new List<SavingsGoalDto>
    {
      new SavingsGoalDto
      {
        Id = 1,
        Name = "Emergency Fund",
        TargetAmount = 10000m,
        CurrentAmount = 5000m
      }
    };
    _mockSavingsService.Setup(s => s.GetSavingsGoalsAsync(TestUserId))
      .ReturnsAsync(goals);

    // Act
    var result = await _executor.ExecuteToolAsync(TestUserId, "get_savings_goals", new Dictionary<string, object>());

    // Assert
    result.Should().Contain("Emergency Fund");
  }

  #endregion

  #region Earmark Transaction Tool Tests

  [Fact]
  public void GetAvailableTools_ShouldIncludeEarmarkTools()
  {
    var tools = _executor.GetAvailableTools();

    tools.Should().Contain(t => t.Name == "earmark_transaction_to_savings_goal");
    tools.Should().Contain(t => t.Name == "remove_transaction_savings_earmark");
  }

  [Fact]
  public async Task ExecuteToolAsync_EarmarkIncome_Unconfirmed_ReturnsDepositPreview()
  {
    // Income transaction (credit) → preview should describe a DEPOSIT.
    var tx = new TransactionDto
    {
      Id = 5,
      Amount = 5000m,
      Type = TransactionType.Credit,
      CategoryId = (int)TransactionCategory.Bonus,
      Description = "RSU vest",
    };
    _mockTransactionService.Setup(s => s.GetTransactionByIdAsync(5, TestUserId)).ReturnsAsync(tx);
    _mockFamilyAuthService.Setup(a => a.CanAccessGoalAsync(TestUserId, 2, ResourcePermission.Edit))
      .ReturnsAsync(AuthorizationResult.Allowed());
    _mockSavingsService.Setup(s => s.GetSavingsGoalsAsync(TestUserId))
      .ReturnsAsync(new List<SavingsGoalDto> { new() { Id = 2, Name = "Pay Down Debt" } });

    var result = await _executor.ExecuteToolAsync(TestUserId, "earmark_transaction_to_savings_goal",
      new Dictionary<string, object> { { "transactionId", 5 }, { "savingsGoalId", 2 }, { "confirmed", false } });

    result.Should().Contain("RequiresConfirmation");
    result.Should().Contain("Pay Down Debt");
    result.Should().Contain("deposit");
    _mockSavingsService.Verify(s => s.DepositFromTransactionAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task ExecuteConfirmedToolAsync_EarmarkIncome_Confirmed_DepositsIntoGoal()
  {
    var tx = new TransactionDto
    {
      Id = 5,
      Amount = 5000m,
      Type = TransactionType.Credit,
      CategoryId = (int)TransactionCategory.Bonus,
      Description = "RSU vest",
    };
    _mockTransactionService.Setup(s => s.GetTransactionByIdAsync(5, TestUserId)).ReturnsAsync(tx);
    _mockFamilyAuthService.Setup(a => a.CanAccessGoalAsync(TestUserId, 2, ResourcePermission.Edit))
      .ReturnsAsync(AuthorizationResult.Allowed());
    _mockSavingsService.Setup(s => s.GetSavingsGoalsAsync(TestUserId))
      .ReturnsAsync(new List<SavingsGoalDto> { new() { Id = 2, Name = "Pay Down Debt" } });
    _mockSavingsService.Setup(s => s.DepositFromTransactionAsync(2, 5000m, 5))
      .ReturnsAsync(new SavingsGoalDto { Id = 2, Name = "Pay Down Debt", CurrentAmount = 5000m, TargetAmount = 20000m });
    _mockTransactionService.Setup(s => s.LinkToSavingsGoalAsync(5, 2, TestUserId))
      .ReturnsAsync(new TransactionDto { Id = 5, SavingsGoalId = 2 });

    var result = await _executor.ExecuteConfirmedToolAsync(TestUserId, "earmark_transaction_to_savings_goal",
      new Dictionary<string, object> { { "transactionId", 5 }, { "savingsGoalId", 2 } });

    result.Should().Contain("Success");
    _mockSavingsService.Verify(s => s.DepositFromTransactionAsync(2, 5000m, 5), Times.Once);
    _mockTransactionService.Verify(s => s.LinkToSavingsGoalAsync(5, 2, TestUserId), Times.Once);
  }

  [Fact]
  public async Task ExecuteConfirmedToolAsync_EarmarkExpense_Confirmed_WithdrawsFromGoal()
  {
    // Expense transaction (debit) → should WITHDRAW from the goal.
    var tx = new TransactionDto
    {
      Id = 8,
      Amount = 25000m,
      Type = TransactionType.Debit,
      CategoryId = (int)TransactionCategory.Transportation,
      Description = "Car dealership",
    };
    _mockTransactionService.Setup(s => s.GetTransactionByIdAsync(8, TestUserId)).ReturnsAsync(tx);
    _mockFamilyAuthService.Setup(a => a.CanAccessGoalAsync(TestUserId, 3, ResourcePermission.Edit))
      .ReturnsAsync(AuthorizationResult.Allowed());
    _mockSavingsService.Setup(s => s.GetSavingsGoalsAsync(TestUserId))
      .ReturnsAsync(new List<SavingsGoalDto> { new() { Id = 3, Name = "Car Fund" } });
    _mockSavingsService.Setup(s => s.WithdrawFromGoalAsync(3, 25000m, 8))
      .ReturnsAsync(new SavingsGoalDto { Id = 3, Name = "Car Fund", CurrentAmount = 0m, TargetAmount = 25000m });
    _mockTransactionService.Setup(s => s.LinkToSavingsGoalAsync(8, 3, TestUserId))
      .ReturnsAsync(new TransactionDto { Id = 8, SavingsGoalId = 3 });

    var result = await _executor.ExecuteConfirmedToolAsync(TestUserId, "earmark_transaction_to_savings_goal",
      new Dictionary<string, object> { { "transactionId", 8 }, { "savingsGoalId", 3 } });

    result.Should().Contain("Success");
    _mockSavingsService.Verify(s => s.WithdrawFromGoalAsync(3, 25000m, 8), Times.Once);
    _mockSavingsService.Verify(s => s.DepositFromTransactionAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task ExecuteConfirmedToolAsync_EarmarkSplitTransaction_ReturnsError()
  {
    var tx = new TransactionDto { Id = 9, Amount = 100m, Type = TransactionType.Debit, IsSplit = true };
    _mockTransactionService.Setup(s => s.GetTransactionByIdAsync(9, TestUserId)).ReturnsAsync(tx);

    var result = await _executor.ExecuteConfirmedToolAsync(TestUserId, "earmark_transaction_to_savings_goal",
      new Dictionary<string, object> { { "transactionId", 9 }, { "savingsGoalId", 2 } });

    result.Should().Contain("Error");
    result.Should().Contain("Split");
    _mockSavingsService.Verify(s => s.WithdrawFromGoalAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task ExecuteConfirmedToolAsync_RemoveEarmark_Confirmed_UnlinksAndReverses()
  {
    var tx = new TransactionDto
    {
      Id = 5,
      Amount = 5000m,
      Type = TransactionType.Credit,
      CategoryId = (int)TransactionCategory.Bonus,
      Description = "RSU vest",
      SavingsGoalId = 2,
      SavingsGoalName = "Pay Down Debt",
    };
    _mockTransactionService.Setup(s => s.GetTransactionByIdAsync(5, TestUserId)).ReturnsAsync(tx);
    _mockSavingsService.Setup(s => s.UndoDepositAsync(2, 5))
      .ReturnsAsync(new SavingsGoalDto { Id = 2, Name = "Pay Down Debt", CurrentAmount = 0m });
    _mockTransactionService.Setup(s => s.LinkToSavingsGoalAsync(5, null, TestUserId))
      .ReturnsAsync(new TransactionDto { Id = 5, SavingsGoalId = null });

    var result = await _executor.ExecuteConfirmedToolAsync(TestUserId, "remove_transaction_savings_earmark",
      new Dictionary<string, object> { { "transactionId", 5 } });

    result.Should().Contain("Success");
    _mockSavingsService.Verify(s => s.UndoDepositAsync(2, 5), Times.Once);
    _mockTransactionService.Verify(s => s.LinkToSavingsGoalAsync(5, null, TestUserId), Times.Once);
  }

  [Fact]
  public async Task ExecuteConfirmedToolAsync_RemoveEarmark_NotLinked_ReturnsError()
  {
    var tx = new TransactionDto { Id = 5, Amount = 5000m, Type = TransactionType.Credit, SavingsGoalId = null };
    _mockTransactionService.Setup(s => s.GetTransactionByIdAsync(5, TestUserId)).ReturnsAsync(tx);

    var result = await _executor.ExecuteConfirmedToolAsync(TestUserId, "remove_transaction_savings_earmark",
      new Dictionary<string, object> { { "transactionId", 5 } });

    result.Should().Contain("Error");
    _mockSavingsService.Verify(s => s.UndoDepositAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
  }

  #endregion
}

