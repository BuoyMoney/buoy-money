namespace BuoyAI.Core.Enums;

/// <summary>
/// Defines the level of MFA protection required for an operation
/// </summary>
public enum MfaRequirementLevel
{
    /// <summary>
    /// No MFA required (public operations)
    /// </summary>
    None = 0,
    
    /// <summary>
    /// Tier 1: Standard login with device trust (30-day exemption)
    /// </summary>
    Login = 1,
    
    /// <summary>
    /// Tier 2: Financial operations - always require MFA regardless of device trust
    /// </summary>
    Financial = 2,
    
    /// <summary>
    /// Tier 3: Administrative operations - require fresh MFA (within 5 minutes)
    /// </summary>
    Administrative = 3
}

/// <summary>
/// Specific operations that require MFA
/// </summary>
public enum ProtectedOperation
{
    // Tier 2 - Financial Operations (Always require MFA)
    ConnectBankAccount = 200,
    DisconnectBankAccount = 201,
    RefreshBankData = 202,
    ViewSensitiveFinancialData = 203,
    
    // Tier 3 - Administrative Operations (Require fresh MFA within 5 minutes)
    ChangePassword = 300,
    UpdateEmail = 301,
    ChangeMfaSettings = 302,
    ManagePasskeys = 303,
    DeleteAccount = 304,

    // Making pairing codes for, and confirming, self-hosted servers on the user's BuoyAI Cloud account.
    ManageCloudServers = 305
}
