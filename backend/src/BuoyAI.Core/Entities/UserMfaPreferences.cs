using System.ComponentModel.DataAnnotations;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

/// <summary>
/// User preferences for MFA method selection and security notifications
/// </summary>
public class UserMfaPreferences
{
    public int Id { get; set; }
    
    [Required]
    public int UserId { get; set; }
    
    /// <summary>
    /// Primary MFA method (Email, Totp, Passkey)
    /// </summary>
    public MfaType PrimaryMethod { get; set; } = MfaType.Email;
    
    /// <summary>
    /// Fallback MFA method if primary fails
    /// </summary>
    public MfaType? FallbackMethod { get; set; }
    
    /// <summary>
    /// Send notification when new device logs in
    /// </summary>
    public bool NotifyOnNewDevice { get; set; } = true;
    
    /// <summary>
    /// Send notification when suspicious login detected
    /// </summary>
    public bool NotifyOnSuspiciousLogin { get; set; } = true;
    
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation property
    public User User { get; set; } = null!;
}
