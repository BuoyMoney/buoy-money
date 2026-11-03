using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Represents a user's registered passkey for WebAuthn authentication
/// </summary>
public class UserPasskey
{
    public int Id { get; set; }
    
    [Required]
    public int UserId { get; set; }
    
    [Required]
    [StringLength(100)]
    public string CredentialId { get; set; } = string.Empty; // Base64 encoded credential ID
    
    [Required]
    public string PublicKey { get; set; } = string.Empty; // Base64 encoded public key
    
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty; // User-friendly name like "iPhone Touch ID"
    
    [StringLength(50)]
    public string? DeviceType { get; set; } // "mobile", "desktop", "security_key"
    
    public int SignCount { get; set; } // WebAuthn signature counter
    
    public bool IsActive { get; set; } = true;
    
    [Required]
    public DateTime CreatedAt { get; set; }
    
    public DateTime? LastUsedAt { get; set; }
    
    [StringLength(45)]
    public string? IpAddress { get; set; }
    
    [StringLength(500)]
    public string? UserAgent { get; set; }
    
    // Navigation property
    public User User { get; set; } = null!;
}
