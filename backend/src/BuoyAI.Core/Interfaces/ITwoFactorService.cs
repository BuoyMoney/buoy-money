using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for managing two-factor authentication
/// </summary>
public interface ITwoFactorService
{
    /// <summary>
    /// Generates and sends a 2FA code via email
    /// </summary>
    Task<string> GenerateAndSendCodeAsync(int userId, string purpose, string? ipAddress = null, string? userAgent = null);
    
    /// <summary>
    /// Verifies a 2FA code
    /// </summary>
    Task<bool> VerifyCodeAsync(int userId, string code, string purpose);
    
    /// <summary>
    /// Invalidates all codes for a user and purpose
    /// </summary>
    Task InvalidateCodesAsync(int userId, string? purpose = null);
    
    /// <summary>
    /// Checks if the user needs to upgrade their MFA
    /// </summary>
    Task<bool> RequiresUpgradeAsync(int userId);
    
    /// <summary>
    /// Checks if the user can access Plaid features (phishing-resistant MFA required)
    /// </summary>
    Task<bool> CanAccessPlaidFeaturesAsync(int userId);
    
    /// <summary>
    /// Sets up email-based MFA for a user (30-day grace period)
    /// </summary>
    Task EnableEmailMfaAsync(int userId);
    
    /// <summary>
    /// Upgrades user to TOTP-based MFA
    /// </summary>
    Task<TotpSetupDto> EnableTotpMfaAsync(int userId);
    
    /// <summary>
    /// Completes TOTP setup after user verification
    /// </summary>
    Task<string[]> CompleteTotpSetupAsync(int userId, string verificationCode);
    
    /// <summary>
    /// Generates recovery codes for the user
    /// </summary>
    Task<string[]> GenerateRecoveryCodesAsync(int userId);

    /// <summary>
    /// Disables ALL of the user's multi-factor authentication — clears TOTP, the MFA
    /// flags/grace period, recovery codes, and removes every registered passkey.
    /// This is a sensitive operation; callers must gate it behind step-up MFA
    /// (see <c>[RequiresMfa(ProtectedOperation.ChangeMfaSettings)]</c>).
    /// Throws <see cref="System.InvalidOperationException"/> if MFA is not enabled.
    /// </summary>
    Task DisableMfaAsync(int userId);

    // Passkey Methods
    
    /// <summary>
    /// Initiates passkey registration for a user
    /// </summary>
    Task<PasskeyRegistrationOptionsDto> InitiatePasskeyRegistrationAsync(int userId, PasskeyRegistrationInitDto request);
    
    /// <summary>
    /// Completes passkey registration with the credential response
    /// </summary>
    Task CompletePasskeyRegistrationAsync(int userId, PasskeyRegistrationCompleteDto request);
    
    /// <summary>
    /// Initiates passkey authentication
    /// </summary>
    Task<PasskeyAuthenticationOptionsDto> InitiatePasskeyAuthenticationAsync(PasskeyAuthenticationInitDto request);
    
    /// <summary>
    /// Completes passkey authentication with the assertion response
    /// </summary>
    Task<User> CompletePasskeyAuthenticationAsync(PasskeyAuthenticationCompleteDto request);
    
    /// <summary>
    /// Gets all registered passkeys for a user
    /// </summary>
    Task<List<UserPasskeyDto>> GetUserPasskeysAsync(int userId);
    
    /// <summary>
    /// Removes a passkey from a user's account
    /// </summary>
    Task RemovePasskeyAsync(int userId, int passkeyId);
    
    /// <summary>
    /// Updates a passkey's name
    /// </summary>
    Task UpdatePasskeyAsync(int userId, int passkeyId, PasskeyUpdateDto request);
    
    /// <summary>
    /// Gets the user's current MFA configuration details
    /// </summary>
    Task<UserMfaDetailsDto> GetUserMfaDetailsAsync(int userId);
}

/// <summary>
/// DTO for TOTP setup information
/// </summary>
public class TotpSetupDto
{
    public string Secret { get; set; } = string.Empty;
    public string QrCodeUrl { get; set; } = string.Empty;
    public string ManualEntryCode { get; set; } = string.Empty;
}
