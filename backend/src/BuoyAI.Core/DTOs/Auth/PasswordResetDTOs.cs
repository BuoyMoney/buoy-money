namespace BuoyAI.Core.DTOs.Auth;

/// <summary>
/// Request to initiate password reset
/// </summary>
public class RequestPasswordResetRequest
{
    public string Email { get; set; } = string.Empty;
}

/// <summary>
/// Request to complete password reset
/// </summary>
public class ResetPasswordRequest
{
    public string Token { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// Response for token validation
/// </summary>
public class ValidateTokenResponse
{
    public bool IsValid { get; set; }
}
