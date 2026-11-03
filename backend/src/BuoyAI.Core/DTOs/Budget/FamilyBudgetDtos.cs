using BuoyAI.Core.Enums;
using BuoyAI.Core.Entities;

namespace BuoyAI.Core.DTOs.Budget;

/// <summary>
/// Overview of a family member's budgets for the family dashboard.
/// </summary>
public class FamilyMemberBudgetSummaryDto
{
  public int UserId { get; set; }
  public string UserName { get; set; } = string.Empty;
  public List<ChildBudgetSummaryDto> Budgets { get; set; } = new();
  public decimal TotalAllocated { get; set; }
  public decimal TotalSpent { get; set; }
  public decimal TotalRemaining { get; set; }
}

/// <summary>
/// Summary of a child budget for family overview.
/// </summary>
public class ChildBudgetSummaryDto
{
  public int Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public decimal AllocatedAmount { get; set; }
  public decimal SpentAmount { get; set; }
  public decimal RemainingAmount { get; set; }
  public double PercentUsed { get; set; }
  public ChildBudgetRolloverMode RolloverMode { get; set; }
  public bool IsSharedWithFamily { get; set; }
}

/// <summary>
/// Family budget overview with all members' budgets.
/// </summary>
public class FamilyBudgetOverviewDto
{
  public int FamilyId { get; set; }
  public string FamilyName { get; set; } = string.Empty;
  public List<FamilyMemberBudgetSummaryDto> Members { get; set; } = new();
  public decimal TotalFamilyAllocated { get; set; }
  public decimal TotalFamilySpent { get; set; }
}
