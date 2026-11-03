using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Represents a trusted device for MFA exemption
/// </summary>
public class TrustedDevice
{
    public int Id { get; set; }
    
    [Required]
    public int UserId { get; set; }
    
    /// <summary>
    /// Unique fingerprint of the device (hash of user agent + IP + browser details)
    /// </summary>
    [Required]
    [StringLength(64)]
    public string DeviceFingerprint { get; set; } = string.Empty;
    
    /// <summary>
    /// User-friendly device name
    /// </summary>
    [StringLength(200)]
    public string? DeviceName { get; set; }
    
    /// <summary>
    /// Type of device (Mobile, Desktop, Tablet)
    /// </summary>
    [StringLength(50)]
    public string? DeviceType { get; set; }
    
    [StringLength(45)]
    public string? IpAddress { get; set; }
    
    [StringLength(500)]
    public string? UserAgent { get; set; }
    
    /// <summary>
    /// City/Country from IP geolocation
    /// </summary>
    [StringLength(200)]
    public string? Location { get; set; }
    
    /// <summary>
    /// When the device was last used for login
    /// </summary>
    public DateTime LastUsedAt { get; set; }
    
    /// <summary>
    /// When the device trust expires (30 days from creation)
    /// </summary>
    [Required]
    public DateTime ExpiresAt { get; set; }
    
    /// <summary>
    /// Whether the device is still trusted
    /// </summary>
    public bool IsActive { get; set; } = true;
    
    [Required]
    public DateTime CreatedAt { get; set; }
    
    public DateTime UpdatedAt { get; set; }
    
    // Navigation property
    public User User { get; set; } = null!;
}
