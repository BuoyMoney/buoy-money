namespace BuoyAI.Core.Enums;

public enum FamilyMemberRole
{
    Owner = 0,           // Created the family, full control (billing + family management)
    BudgetAdmin = 1,     // Can modify budgets and financial data (not billing)
    Member = 2,          // Full permissions: view and edit financial data
    ReadOnly = 3         // View-only access (e.g., kids, observers)
}
