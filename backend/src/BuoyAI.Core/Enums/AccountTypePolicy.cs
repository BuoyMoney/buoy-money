namespace BuoyAI.Core.Enums;

/// <summary>
/// Provider-neutral policy for which aggregated account types BuoyAI budgets with.
///
/// BuoyAI is a month-to-month budgeting app (#310): its scope is transactional deposit accounts
/// (checking / savings and cash-like depository) plus credit cards. Investment / brokerage accounts
/// (e.g. Robinhood) and loans / mortgages are intentionally out of scope — they add balance noise
/// and carry no budgetable spending. Any aggregator (Plaid today, MX later) MUST drop non-budget
/// accounts at ingestion so they never get a local <c>Account</c> row and never surface.
///
/// This is deliberately keyed on the neutral <see cref="AccountType"/> (not a provider's raw type)
/// so every aggregator funnels through the same rule after mapping to our enum.
/// </summary>
public static class AccountTypePolicy
{
    /// <summary>
    /// True for account types we aggregate and budget with. Note <see cref="AccountType.Cash"/> is
    /// the synthetic manual-entry account (never comes from an aggregator) — included because manual
    /// cash spending is budgeted. <see cref="AccountType.Investment"/> and <see cref="AccountType.Other"/>
    /// (which is where loans / mortgages land after mapping) are excluded.
    /// </summary>
    public static bool IsBudgetRelevant(AccountType type) => type switch
    {
        AccountType.Checking
            or AccountType.Savings
            or AccountType.CreditCard
            or AccountType.Cash => true,
        _ => false, // Investment (brokerage/retirement), Other (loans/mortgages/unmapped)
    };
}
