namespace BuoyAI.Core.DTOs.Export;

/// <summary>
/// Everything the export needs to know before it starts writing: who the caller is, which family
/// rows they may see, and the small id sets that child tables are filtered by. Resolving it first
/// lets the controller answer 404 before any bytes are sent.
/// </summary>
public sealed class ExportScope
{
    public int UserId { get; init; }
    public bool HasFamily { get; init; }
    public int FamilyId { get; init; }
    public bool ReadOnly { get; init; }
    /// <summary>True when the account has any second factor (MFA flag or a registered passkey).</summary>
    public bool HasMfa { get; init; }
    public required ExportUserDto User { get; init; }
    public ExportFamilyDto? Family { get; init; }
    public required ExportPreferencesDto Preferences { get; init; }
    public required IReadOnlyList<ExportAccountDto> Accounts { get; init; }
    public required IReadOnlyDictionary<int, int> AccountOwners { get; init; }
    public required IReadOnlyList<int> BudgetIds { get; init; }
    public required IReadOnlyList<int> GoalIds { get; init; }
    public required IReadOnlyList<int> SubscriptionIds { get; init; }
    /// <summary>Transactions a ReadOnly family member must not see (hidden by their owner).</summary>
    public required IReadOnlySet<int> HiddenTransactionIds { get; init; }
}
