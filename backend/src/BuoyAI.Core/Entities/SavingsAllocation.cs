namespace BuoyAI.Core.Entities;

public class SavingsAllocation
{
    public int Id { get; set; }
    public int SavingsGoalId { get; set; }
    public int? BudgetId { get; set; }
    public decimal AllocatedAmount { get; set; }
    public decimal LeftoverBudget { get; set; }
    public DateTime AllocationDate { get; set; }
    public string Source { get; set; } = "budget_surplus";
    public DateTime CreatedAt { get; set; }
    
    // Navigation properties
    public SavingsGoal SavingsGoal { get; set; } = null!;
    public Budget? Budget { get; set; }
}
