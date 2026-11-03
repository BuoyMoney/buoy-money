using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Extensions;
using BuoyAI.Core.ValueObjects;

namespace BuoyAI.API.Controllers;

[Authorize]
public class ReportsController : BaseController
{
  /// <summary>
  /// Maximum number of transactions to fetch for reports.
  /// Reports need all transactions in the date range for accurate aggregation.
  /// </summary>
  private const int ReportMaxTransactions = 1000;

  private readonly ITransactionService _transactionService;
  private readonly IBudgetService _budgetService;
  private readonly ILogger<ReportsController> _logger;

  public ReportsController(
    ITransactionService transactionService,
    IBudgetService budgetService,
    ILogger<ReportsController> logger)
  {
    _transactionService = transactionService;
    _budgetService = budgetService;
    _logger = logger;
  }

  [HttpGet("spending-by-category")]
  public async Task<IActionResult> GetSpendingByCategory([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
  {
    var userId = GetCurrentUserId();

    // Convert dates to UTC using helper (defaults to 1 month back for reports)
    var (effectiveStartDate, effectiveEndDate) = DateTimeExtensions.GetEffectiveDateRange(startDate, endDate, defaultDaysBack: 30);

    var filter = new TransactionFilterRequest
    {
      StartDate = effectiveStartDate,
      EndDate = effectiveEndDate,
      PageSize = ReportMaxTransactions
    };

    var transactions = await _transactionService.GetTransactionsAsync(userId, filter);

    var categorySpending = transactions
      .Where(t => t.Type == TransactionType.Debit) // Only outgoing transactions
      // Transfers/card payments are money movement, not spending — including
      // them made "Transfer" the top category of every report.
      .Where(t => !TransactionAmountExtensions.IsNonBudgetableCategoryId(t.CategoryId ?? 0))
      .GroupBy(t => t.CategoryId ?? 0)
      .Select(g => new
      {
        Category = g.First().CategoryName ?? "Uncategorized",
        TotalSpent = g.Sum(t => Math.Abs(t.Amount)), // Use absolute value for expenses
        TransactionCount = g.Count()
      })
      .OrderByDescending(x => x.TotalSpent)
      .ToList();

    var totalSpent = categorySpending.Sum(c => c.TotalSpent);

    // Format response to match frontend expectations: SpendingByCategory[]
    var result = categorySpending.Select(c => new
    {
      category = c.Category,
      amount = c.TotalSpent,
      percentage = totalSpent > 0 ? (double)(c.TotalSpent / totalSpent * 100) : 0
    }).ToList();

    return Ok(result);
  }

  [HttpGet("budget-summary")]
  public async Task<IActionResult> GetBudgetSummary()
  {
    var userId = GetCurrentUserId();
    var activeBudget = await _budgetService.GetActiveBudgetAsync(userId);

    if (activeBudget == null)
    {
      return NotFound(new { message = "No active budget found" });
    }

    // Expense metrics exclude income categories — income received (e.g. Salary) would
    // otherwise inflate totalSpent/utilization. Matches the budget DTO's TotalExpenses.
    var expenseCategories = activeBudget.Categories.Where(c => !c.IsIncome).ToList();
    var totalAllocated = expenseCategories.Sum(c => c.AllocatedAmount);
    var totalSpent = expenseCategories.Sum(c => c.SpentAmount);
    var totalRemaining = totalAllocated - totalSpent;

    var overBudgetCategories = expenseCategories
      .Where(c => c.SpentAmount > c.AllocatedAmount)
      .Select(c => new
      {
        c.CategoryName,
        c.AllocatedAmount,
        c.SpentAmount,
        OverAmount = c.SpentAmount - c.AllocatedAmount
      })
      .ToList();

    var underBudgetCategories = expenseCategories
      .Where(c => c.SpentAmount < c.AllocatedAmount)
      .Select(c => new
      {
        c.CategoryName,
        c.AllocatedAmount,
        c.SpentAmount,
        RemainingAmount = c.AllocatedAmount - c.SpentAmount
      })
      .OrderByDescending(c => c.RemainingAmount)
      .ToList();

    return Ok(new
    {
      BudgetPeriod = new
      {
        activeBudget.Name,
        StartDate = activeBudget.CurrentPeriodStart,
        EndDate = activeBudget.CurrentPeriodEnd
      },
      Summary = new
      {
        TotalIncome = activeBudget.TotalIncome,
        TotalAllocated = totalAllocated,
        TotalSpent = totalSpent,
        TotalRemaining = totalRemaining,
        BudgetUtilization = totalAllocated > 0 ? (totalSpent / totalAllocated) * 100 : 0
      },
      Categories = activeBudget.Categories.Select(c => new
      {
        c.CategoryName,
        c.AllocatedAmount,
        c.SpentAmount,
        c.RemainingAmount,
        c.PercentageUsed,
        IsOverBudget = c.SpentAmount > c.AllocatedAmount
      }),
      OverBudgetCategories = overBudgetCategories,
      UnderBudgetCategories = underBudgetCategories
    });
  }
}
