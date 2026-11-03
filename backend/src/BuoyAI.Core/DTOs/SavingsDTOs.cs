using System.ComponentModel.DataAnnotations;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.DTOs.Savings;

public class SavingsOverviewDto
{
    public decimal TotalSaved { get; set; }
    public decimal MonthlyGrowth { get; set; }
    public decimal CurrentPeriodLeftover { get; set; }
    public decimal FamilyLeftover { get; set; } // Leftover from family budget
    public decimal PersonalLeftover { get; set; } // Leftover from personal/allowance budget
    public bool HasFamilyBudget { get; set; } // Whether user is in a family with access to family budget
    public decimal ProjectedMonthlyGrowth { get; set; }
    public decimal TotalAllocatedPerPeriod { get; set; }
    public decimal UnallocatedAmount { get; set; }
    public int ActiveGoalsCount { get; set; }
    public int CompletedGoalsCount { get; set; }
    public List<SavingsGoalDto> Goals { get; set; } = new();
    public List<SavingsAchievementDto> RecentAchievements { get; set; } = new();
}

public class SavingsGoalDto
{
    public int Id { get; set; }
    public SavingsGoalType Type { get; set; } = SavingsGoalType.Personal;
    public int UserId { get; set; } // Creator of the goal
    public int? FamilyId { get; set; } // Family this goal belongs to (for Family type)
    public string OwnerName { get; set; } = string.Empty; // Display name of goal creator
    public bool IsOwner { get; set; } // Whether current user is the owner
    public bool CanContribute { get; set; } // Whether current user can contribute to this goal
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal TargetAmount { get; set; }
    public decimal CurrentAmount { get; set; }
    public decimal AllocationPercentage { get; set; }
    public DateTime? TargetDate { get; set; }
    public string IconName { get; set; } = string.Empty;
    public string ColorTheme { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsCompleted { get; set; }
    public bool CountWithdrawalsTowardBudget { get; set; }

    // ── Optional account link (reconciliation, not source of truth) ──
    /// <summary>The account this goal's money is meant to live in, or null when unlinked.</summary>
    public int? AccountId { get; set; }
    public string? AccountName { get; set; }

    /// <summary>Real balance of the linked account. Null when unlinked.</summary>
    public decimal? AccountBalance { get; set; }

    /// <summary>
    /// Sum of <see cref="CurrentAmount"/> across EVERY goal pointing at the same account, not just
    /// this one. Several goals can share an account, so this is the only figure the balance can
    /// honestly be compared against — one goal's amount against the whole balance would report
    /// drift that isn't there.
    /// </summary>
    public decimal? AccountAllocatedToGoals { get; set; }

    /// <summary>
    /// Balance minus everything allocated to goals on that account. Positive = money in the
    /// account no goal has claimed; negative = goals promise more than the account holds. Null
    /// when unlinked.
    /// </summary>
    public decimal? AccountUnallocated { get; set; }

    /// <summary>
    /// Money already taken out of the envelope by earmarked purchases that has not yet physically
    /// left the linked account (no covering transfer since the last sync). Explains that much of
    /// <see cref="AccountUnallocated"/>: the account still holds it, but it is spoken for. Null
    /// when unlinked.
    /// </summary>
    public decimal? PendingTransferAmount { get; set; }

    // Calculated fields
    public decimal ProgressPercentage { get; set; }
    public decimal RemainingAmount { get; set; }
    public int? DaysRemaining { get; set; }
    public decimal DailyRequiredSavings { get; set; }
    public bool IsOnTrack { get; set; }
    public string ProgressStatus { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>Server-side last-modified timestamp (UTC); client uses it as baseUpdatedAt for sync (#493).</summary>
    public DateTime UpdatedAt { get; set; }
}

public class CreateSavingsGoalRequest
{
    public SavingsGoalType Type { get; set; } = SavingsGoalType.Personal;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    [Range(0.01, double.MaxValue, ErrorMessage = "Target amount must be greater than 0.")]
    public decimal TargetAmount { get; set; }
    public decimal InitialAmount { get; set; } // Optional starting amount already saved toward goal
    public decimal AllocationPercentage { get; set; }
    public DateTime? TargetDate { get; set; }
    public string IconName { get; set; } = "target";
    public string ColorTheme { get; set; } = "primary";
    public bool CountWithdrawalsTowardBudget { get; set; } = false;

    /// <summary>Optional account to reconcile this goal against. Null = unlinked, which is fine.</summary>
    public int? AccountId { get; set; }
}

public class UpdateSavingsGoalRequest
{
    public SavingsGoalType? Type { get; set; } // Optional - allows changing goal type
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    [Range(0.01, double.MaxValue, ErrorMessage = "Target amount must be greater than 0.")]
    public decimal TargetAmount { get; set; }
    public decimal AllocationPercentage { get; set; }
    public DateTime? TargetDate { get; set; }
    public string IconName { get; set; } = string.Empty;
    public string ColorTheme { get; set; } = string.Empty;
    // Nullable so an edit that omits it (e.g. the Flutter edit sheet only changes name/target)
    // preserves the current state. A non-nullable bool bound to absent JSON as false, which
    // silently deactivated the goal — hiding it from the overview and stopping auto-allocation.
    public bool? IsActive { get; set; }
    public bool? CountWithdrawalsTowardBudget { get; set; }

    /// <summary>Account to reconcile against. Absent leaves the existing link untouched.</summary>
    /// <remarks>
    /// Null cannot mean "unlink" here. The Flutter edit sheet omits fields it isn't changing, so
    /// absent-as-null would quietly drop the link on any unrelated edit — exactly the failure the
    /// nullable <see cref="IsActive"/> above exists to prevent. Unlinking is therefore an explicit
    /// <see cref="ClearAccountLink"/> instead.
    /// </remarks>
    public int? AccountId { get; set; }

    /// <summary>Set true to deliberately unlink. Wins over <see cref="AccountId"/>.</summary>
    public bool ClearAccountLink { get; set; }
}

public class SavingsAchievementDto
{
    public int Id { get; set; }
    public string AchievementType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string BadgeIcon { get; set; } = string.Empty;
    public string BadgeColor { get; set; } = string.Empty;
    public DateTime EarnedDate { get; set; }
    public bool IsNotified { get; set; }
}

public class ManualAllocationRequest
{
    public int SavingsGoalId { get; set; }
    public decimal Amount { get; set; }
    public string Source { get; set; } = "manual_addition";
}

// ── Account reconciliation ──────────────────────────────────────────────────

/// <summary>One goal's proposed new balance in a reconcile.</summary>
public class GoalReconcileAdjustment
{
    public int GoalId { get; set; }

    /// <summary>
    /// The balance this goal should end up at. An absolute target rather than a delta so the
    /// request describes the state the user saw and approved — a delta applied to a balance that
    /// moved underneath them would silently produce a total they never agreed to.
    /// </summary>
    public decimal NewAmount { get; set; }
}

public class ReconcileAccountRequest
{
    public List<GoalReconcileAdjustment> Adjustments { get; set; } = new();
}

/// <summary>What a reconcile left behind, so the client can state the outcome plainly.</summary>
public class ReconcileResultDto
{
    public decimal AccountBalance { get; set; }
    public decimal AllocatedToGoals { get; set; }

    /// <summary>Positive = money no goal claims (fine). Negative = goals still over the balance.</summary>
    public decimal Unallocated { get; set; }

    public List<SavingsGoalDto> Goals { get; set; } = new();
}
