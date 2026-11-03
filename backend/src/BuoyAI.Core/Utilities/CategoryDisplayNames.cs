using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Utilities;

/// <summary>
/// Centralized display name mapping for TransactionCategory.
/// Consolidates duplicated GetCategoryDisplayName methods from multiple services.
/// </summary>
public static class CategoryDisplayNames
{
    /// <summary>
    /// Gets a user-friendly display name for a transaction category.
    /// </summary>
    public static string GetDisplayName(this TransactionCategory category)
    {
        return category switch
        {
            // Income categories
            TransactionCategory.Salary => "Salary",
            TransactionCategory.Bonus => "Bonus",
            TransactionCategory.Investment => "Investment Income",
            TransactionCategory.OtherIncome => "Other Income",

            // Fixed expenses
            TransactionCategory.Rent => "Rent/Mortgage",
            TransactionCategory.Utilities => "Utilities",
            TransactionCategory.Insurance => "Insurance",
            TransactionCategory.Subscriptions => "Subscriptions",
            TransactionCategory.LoanPayments => "Loan Payments",

            // Variable expenses
            TransactionCategory.Groceries => "Groceries",
            TransactionCategory.DiningOut => "Dining Out",
            TransactionCategory.Transportation => "Transportation",
            TransactionCategory.Entertainment => "Entertainment",
            TransactionCategory.Shopping => "Shopping",
            TransactionCategory.Healthcare => "Healthcare",
            TransactionCategory.Travel => "Travel",
            TransactionCategory.PersonalCare => "Personal Care",
            TransactionCategory.Education => "Education",
            TransactionCategory.Gifts => "Gifts & Donations",

            // Transfers & payments
            TransactionCategory.CreditPayment => "Credit Payment",
            TransactionCategory.Transfer => "Transfer",

            // Other / Uncategorized
            TransactionCategory.Other => "Other",
            TransactionCategory.Uncategorized => "Uncategorized",

            _ => category.ToString()
        };
    }
}
