namespace BuoyAI.Core.DTOs.Auth;

/// <summary>
/// Device information for trust evaluation
/// </summary>
public class DeviceInfoDto
{
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? DeviceFingerprint { get; set; }
    public bool RememberDevice { get; set; }
}

/// <summary>
/// Result of device trust evaluation
/// </summary>
public class DeviceTrustEvaluationDto
{
    /// <summary>
    /// Whether this device is already trusted
    /// </summary>
    public bool IsTrustedDevice { get; set; }
    
    /// <summary>
    /// Whether MFA should be required
    /// </summary>
    public bool RequiresMfa { get; set; }
    
    /// <summary>
    /// Risk score (0-100)
    /// </summary>
    public int RiskScore { get; set; }
    
    /// <summary>
    /// Reasons for requiring MFA
    /// </summary>
    public List<string> MfaReasons { get; set; } = new();
    
    /// <summary>
    /// ID of the trusted device (if applicable)
    /// </summary>
    public int? TrustedDeviceId { get; set; }
}

/// <summary>
/// Response with user's trusted devices
/// </summary>
public class TrustedDeviceDto
{
    public int Id { get; set; }
    public string DeviceFingerprint { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? DeviceType { get; set; }
    public string? Location { get; set; }
    public DateTime LastUsedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsCurrentDevice { get; set; }
    public int DaysUntilExpiration { get; set; }
    public string? LastUsedIpAddress { get; set; }
    public string? LastUsedLocation { get; set; }
}

/// <summary>
/// Enhanced login request with device information
/// </summary>
public class EnhancedLoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberDevice { get; set; }
    public string? DeviceFingerprint { get; set; }
    
    /// <summary>
    /// Ignored. Older app versions send this after a local biometric unlock. The
    /// server cannot verify it, so it has no effect on whether MFA is required.
    /// Kept only so requests from those versions still bind.
    /// </summary>
    [Obsolete("Ignored by the server: a client-side biometric unlock is not a second factor.")]
    public bool BiometricVerified { get; set; }
}

/// <summary>
/// Enhanced login response with MFA requirements
/// </summary>
public class EnhancedLoginResponse
{
    /// <summary>
    /// Whether MFA is required for this login
    /// </summary>
    public bool RequiresMfa { get; set; }
    
    /// <summary>
    /// Temporary token for MFA verification (only if RequiresMfa is true)
    /// </summary>
    public string? MfaToken { get; set; }
    
    /// <summary>
    /// Preferred MFA method to use (e.g., "Email", "Totp", "Passkey")
    /// </summary>
    public string? PreferredMfaMethod { get; set; }
    
    /// <summary>
    /// List of MFA methods available to the user
    /// </summary>
    public List<string> AvailableMfaMethods { get; set; } = new();
    
    /// <summary>
    /// Full authentication token (only if MFA not required or already completed)
    /// </summary>
    public string? Token { get; set; }
    
    /// <summary>
    /// User profile (only if authenticated)
    /// </summary>
    public UserProfileDto? User { get; set; }
    
    /// <summary>
    /// Token expiration (only if authenticated)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Rotating refresh token (native clients only; web receives an HttpOnly cookie).
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Reasons why MFA is required
    /// </summary>
    public List<string> MfaReasons { get; set; } = new();

    /// <summary>
    /// True only when this sign-in created the account (single sign-on can create one).
    /// </summary>
    public bool IsNewUser { get; set; }
}

/// <summary>
/// Request to verify MFA code and complete login
/// </summary>
public class VerifyMfaLoginRequest
{
    public string MfaToken { get; set; } = string.Empty;
    public string MfaCode { get; set; } = string.Empty;
    public bool RememberDevice { get; set; }
    public string? DeviceFingerprint { get; set; }
}

/// <summary>
/// Request to send MFA code during login (for Email MFA)
/// </summary>
public class SendLoginMfaCodeRequest
{
    public string MfaToken { get; set; } = string.Empty;
}

/// <summary>
/// Request to verify step-up MFA for sensitive operations
/// </summary>
public class VerifyStepUpMfaRequest
{
    public string MfaCode { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
}

public class StepUpPasskeyVerifyRequest
{
    public string AssertionResponse { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
}

/// <summary>
/// Response for step-up MFA verification
/// </summary>
public class StepUpMfaResponse
{
    /// <summary>
    /// Operation-specific token valid for a short period
    /// </summary>
    public string OperationToken { get; set; } = string.Empty;
    
    /// <summary>
    /// When the operation token expires
    /// </summary>
    public DateTime ExpiresAt { get; set; }
    
    /// <summary>
    /// The operation this token is valid for
    /// </summary>
    public string Operation { get; set; } = string.Empty;
}
