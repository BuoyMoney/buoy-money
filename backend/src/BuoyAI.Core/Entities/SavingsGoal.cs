using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

public class SavingsGoal
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? FamilyId { get; set; } // Foreign key to Family (required for Family type goals)
    public SavingsGoalType Type { get; set; } = SavingsGoalType.Personal; // Personal or Family goal
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal TargetAmount { get; set; }
    public decimal CurrentAmount { get; set; }
    public decimal AllocationPercentage { get; set; }
    public DateTime? TargetDate { get; set; }
    public string IconName { get; set; } = "target";
    public string ColorTheme { get; set; } = "primary";
    public bool IsActive { get; set; } = true;
    public bool IsCompleted { get; set; } = false;
    public bool CountWithdrawalsTowardBudget { get; set; } = false;

    /// <summary>
    /// Optional real account this goal's money is meant to live in.
    /// </summary>
    /// <remarks>
    /// This is a RECONCILIATION link, not the source of truth. <see cref="CurrentAmount"/> stays
    /// the authority: it is an envelope balance moved by explicit contributions, withdrawals, and
    /// budget-surplus allocations, and deriving it from the account balance would double-count
    /// against all three. What the link buys is the ability to notice drift — several goals can
    /// point at one account, so the meaningful comparison is the account balance against the SUM
    /// of goals pointing at it, never a single goal against the whole balance.
    ///
    /// Null is a first-class state: saving inside a checking account, in cash, or toward something
    /// with no account opened yet are all normal, so goals must never require one.
    /// </remarks>
    public int? AccountId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public User User { get; set; } = null!;
    public Family? Family { get; set; }
    public Account? Account { get; set; }
    public ICollection<SavingsAllocation> Allocations { get; set; } = new List<SavingsAllocation>();
}
