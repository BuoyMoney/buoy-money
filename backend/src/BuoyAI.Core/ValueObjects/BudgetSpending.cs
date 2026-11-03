using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using static BuoyAI.Core.ValueObjects.TransactionAmountExtensions;

namespace BuoyAI.Core.ValueObjects;

/// <summary>
/// What a budget counts as its own spending. The Budgets screen, the count-up card, the daily
/// allowance and the month close must agree, or a category shows one number all month and closes
/// on another. Load the transactions with the user-or-family query for a budget in a family (the
/// owner's accounts otherwise), then scope them here.
/// </summary>
public static class BudgetSpending
{
  /// <summary>
  /// The claim rule (#535): a Family budget counts the unclaimed charges (shared spend); any other
  /// budget in a family (a member's personal or allowance budget) counts only the charges claimed
  /// to its owner; a budget outside a family counts everything on its owner's accounts. Pending
  /// charges are left out when the owner turned on ExcludePendingFromBudget.
  /// </summary>
  public static IEnumerable<Transaction> ScopeToBudget(
    this IEnumerable<Transaction> loaded, Budget budget, bool excludePending = false)
  {
    var transactions = excludePending ? loaded.Where(t => !t.IsPending) : loaded;
    if (budget.Type == BudgetType.Family)
      return transactions.Where(t => t.ClaimedByUserId == null);
    if (budget.FamilyId.HasValue)
      return transactions.Where(t => t.ClaimedByUserId == budget.UserId);
    return transactions;
  }

  /// <summary>
  /// Spending in one of the budget's categories, from transactions already scoped to the budget.
  /// A family-funded budget (an allowance, or a personal budget funded by a family allowance line)
  /// buckets by its owner's own sub-category, with not-yet-sorted charges under Uncategorized, the
  /// same as the Budgets screen.
  /// </summary>
  public static decimal SpentInCategory(
    this IEnumerable<Transaction> scoped, Budget budget, BudgetCategory category, bool familyFunded)
  {
    var key = new CategoryKey(category.GetEffectiveCategoryId());
    if (familyFunded)
      return scoped.ExpensesByAllowanceCategoryKey(budget.UserId).GetValueOrDefault(key, 0m);
    if (budget.Type == BudgetType.Personal && category.CategoryId >= 1000)
      return scoped.TotalSpentInPersonalCategoryKey(key);
    return scoped.TotalSpentInCategoryKey(key);
  }

  /// <summary>
  /// The transactions <see cref="SpentInCategory"/> counts for one of the budget's categories (the
  /// same bucketing), for per-day figures such as the count-up card's streaks and zero-spend days.
  /// </summary>
  public static IEnumerable<Transaction> InCategory(
    this IEnumerable<Transaction> scoped, Budget budget, BudgetCategory category, bool familyFunded)
  {
    var key = new CategoryKey(category.GetEffectiveCategoryId());
    if (familyFunded)
      return scoped.WhereAllowanceCategoryKey(budget.UserId, key);
    if (budget.Type == BudgetType.Personal && category.CategoryId >= 1000)
      return scoped.WherePersonalCategoryKey(key);
    return scoped.WhereCategoryKey(key);
  }
}
