namespace BuoyAI.Core.Enums;

/// <summary>
/// Represents the type of multi-factor authentication enabled for a user
/// </summary>
public enum MfaType
{
    /// <summary>
    /// No MFA enabled
    /// </summary>
    None = 0,
    
    /// <summary>
    /// Email-based codes (temporary, max 30 days)
    /// </summary>
    Email = 1,
    
    /// <summary>
    /// Time-based One-Time Password (TOTP) via authenticator app - phishing-resistant
    /// </summary>
    Totp = 2,
    
    /// <summary>
    /// WebAuthn Passkeys (Face ID, Touch ID, Windows Hello) - phishing-resistant
    /// </summary>
    Passkey = 3
}
