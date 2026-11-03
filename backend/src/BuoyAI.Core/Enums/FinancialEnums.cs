namespace BuoyAI.Core.Enums;

public enum TransactionType
{
  Debit = 0,
  Credit = 1
}

public enum AccountType
{
  Checking = 0,
  Savings = 1,
  CreditCard = 2,
  Investment = 3,
  Other = 4,
  // Synthetic per-user account that holds manual "cash" transactions entered without a linked
  // bank account. Never comes from Plaid. See TransactionService.GetOrCreateCashAccountAsync.
  Cash = 5
}

public enum TransactionCategory
{
  // Income
  Salary = 1,
  Bonus = 2,
  Investment = 3,
  OtherIncome = 4,

  // Fixed Expenses
  Rent = 10,
  Utilities = 11,
  Insurance = 12,
  Subscriptions = 13,
  LoanPayments = 14,

  // Variable Expenses
  Groceries = 20,
  DiningOut = 21,
  Transportation = 22,
  Entertainment = 23,
  Shopping = 24,
  Healthcare = 25,
  Travel = 26,
  PersonalCare = 27,
  Education = 28,
  Gifts = 29,

  // Transfers & Payments
  CreditPayment = 30,
  Transfer = 31,

  // Other (catch-all for miscellaneous)
  Other = 98,

  // Uncategorized (not yet categorized)
  Uncategorized = 99
}

public enum BudgetPeriod
{
  Weekly = 0,
  BiWeekly = 1,
  Monthly = 2,
  Quarterly = 3,
  Yearly = 4
}

public enum BudgetType
{
  Personal = 0,     // Standard personal budget
  Family = 1,       // Shared family budget visible to all members
  Allowance = 2     // Child budget funded by a parent budget
}

public enum BudgetVisibility
{
  OwnerOnly = 0,    // Only the owner can see this budget
  Family = 1,       // All family members can see this budget
  Custom = 2        // Specific users defined in BudgetShare table
}

public enum SubscriptionFrequency
{
  Weekly = 0,
  BiWeekly = 1,
  Monthly = 2,
  Quarterly = 3,
  SemiAnnual = 4,
  Annual = 5,
  Custom = 99
}

public enum SubscriptionStatus
{
  Active = 0,
  Paused = 1,
  Cancelled = 2,
  Expired = 3
}

/// <summary>
/// Status of a suggested/detected subscription before user confirmation
/// </summary>
public enum SuggestedSubscriptionStatus
{
  /// <summary>
  /// Awaiting user review
  /// </summary>
  Pending = 0,
  
  /// <summary>
  /// User confirmed this is a real subscription (converted to Subscription entity)
  /// </summary>
  Confirmed = 1,
  
  /// <summary>
  /// User dismissed this as not a subscription
  /// </summary>
  Dismissed = 2,
  
  /// <summary>
  /// User snoozed this - will show again after SnoozeUntil date
  /// </summary>
  Snoozed = 3
}

public enum SubscriptionCategory
{
  Streaming = 1,        // Netflix, Hulu, Spotify, etc.
  Software = 2,         // SaaS, apps, cloud services
  Utilities = 3,        // Phone, internet, power, etc.
  Insurance = 4,        // Health, car, home insurance
  Fitness = 5,          // Gym memberships, fitness apps
  News = 6,             // Newspapers, magazines
  Gaming = 7,           // Gaming subscriptions, platforms
  Shopping = 8,         // Amazon Prime, membership fees
  Education = 9,        // Online courses, learning platforms
  Professional = 10,    // LinkedIn, professional tools
  Other = 99
}

/// <summary>
/// Type of savings goal - determines funding source and visibility
/// </summary>
public enum SavingsGoalType
{
  /// <summary>
  /// Personal goal funded by individual's allowance/personal budget leftover.
  /// Only visible to the owner.
  /// </summary>
  Personal = 0,
  
  /// <summary>
  /// Family goal funded by family budget leftover.
  /// Visible to all family members.
  /// </summary>
  Family = 1
}
