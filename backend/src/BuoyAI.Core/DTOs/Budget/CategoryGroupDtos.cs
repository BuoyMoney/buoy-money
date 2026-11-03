namespace BuoyAI.Core.DTOs.Budget;

/// <summary>
/// Represents a category group for organizing budget categories.
/// </summary>
public class CategoryGroupDto
{
  public int Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public string? Icon { get; set; }
  public int SortOrder { get; set; }
  public bool IsSystem { get; set; }  // True if system-defined (Income, Housing, etc.)
  public List<BudgetCategoryDto> Categories { get; set; } = new();
}

/// <summary>
/// Request to create a new custom category group.
/// </summary>
public class CreateCategoryGroupRequest
{
  public string Name { get; set; } = string.Empty;
  public string? Icon { get; set; }
  public int? SortOrder { get; set; }  // If not provided, will be placed at end
}

/// <summary>
/// Request to update an existing category group.
/// </summary>
public class UpdateCategoryGroupRequest
{
  public string? Name { get; set; }
  public string? Icon { get; set; }
  public int? SortOrder { get; set; }
}

/// <summary>
/// Request to reorder category groups.
/// </summary>
public class ReorderCategoryGroupsRequest
{
  public List<int> GroupIds { get; set; } = new();  // Groups in desired order
}

/// <summary>
/// Request to move a category to a different group.
/// </summary>
public class MoveCategoryToGroupRequest
{
  public int CategoryId { get; set; }
  public int? GroupId { get; set; }  // null to move to "Other" group
  public int? SortOrder { get; set; }  // Position within the group
}

/// <summary>
/// Response containing budget categories organized by groups for the budget redesign.
/// </summary>
public class GroupedBudgetOverviewDto
{
  public int BudgetId { get; set; }
  public string BudgetName { get; set; } = string.Empty;
  public decimal TotalAllocated { get; set; }
  public decimal TotalSpent { get; set; }
  public decimal TotalRemaining { get; set; }
  public List<CategoryGroupDto> Groups { get; set; } = new();
}
