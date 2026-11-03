using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.DTOs;

/// <summary>
/// DTO for initiating passkey registration
/// </summary>
public class PasskeyRegistrationInitDto
{
    [Required]
    public string DisplayName { get; set; } = string.Empty;
    
    public string? DeviceName { get; set; }
}

/// <summary>
/// DTO containing the WebAuthn challenge and options for passkey registration
/// </summary>
public class PasskeyRegistrationOptionsDto
{
    [Required]
    public string Challenge { get; set; } = string.Empty;
    
    [Required]
    public string CredentialCreationOptions { get; set; } = string.Empty;
}

/// <summary>
/// DTO for completing passkey registration with the credential response
/// </summary>
public class PasskeyRegistrationCompleteDto
{
    [Required]
    public string CredentialResponse { get; set; } = string.Empty;
    
    public string? DeviceName { get; set; }
}

/// <summary>
/// DTO for initiating passkey authentication
/// </summary>
public class PasskeyAuthenticationInitDto
{
    public string? Username { get; set; } // Optional - can be discoverable credential

    // When set (e.g. step-up MFA for an already-authenticated user), the
    // assertion options are scoped to this user's registered credentials via
    // allowCredentials, so the authenticator cannot return a different
    // (e.g. stale/synced) passkey for the relying party.
    public int? UserId { get; set; }
}

/// <summary>
/// DTO containing the WebAuthn challenge and options for passkey authentication
/// </summary>
public class PasskeyAuthenticationOptionsDto
{
    [Required]
    public string Challenge { get; set; } = string.Empty;
    
    [Required]
    public string AssertionOptions { get; set; } = string.Empty;
}

/// <summary>
/// DTO for completing passkey authentication with the assertion response
/// </summary>
public class PasskeyAuthenticationCompleteDto
{
    [Required]
    public string AssertionResponse { get; set; } = string.Empty;
}

/// <summary>
/// DTO representing a user's registered passkey
/// </summary>
public class UserPasskeyDto
{
    public int Id { get; set; }
    
    [Required]
    public string Name { get; set; } = string.Empty;
    
    public string? DeviceType { get; set; }
    
    public DateTime CreatedAt { get; set; }
    
    public DateTime? LastUsedAt { get; set; }
    
    public long SignCount { get; set; }
    
    public bool IsActive { get; set; }
}

/// <summary>
/// DTO for passkey management operations
/// </summary>
public class PasskeyUpdateDto
{
    [Required]
    public string Name { get; set; } = string.Empty;
}
