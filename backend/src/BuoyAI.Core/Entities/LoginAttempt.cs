using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Tracks login attempts for security monitoring and risk scoring
/// </summary>
public class LoginAttempt
{
    public int Id { get; set; }
    
    [Required]
    public int UserId { get; set; }
    
    [StringLength(45)]
    public string? IpAddress { get; set; }
    
    [StringLength(500)]
    public string? UserAgent { get; set; }
    
    [StringLength(200)]
    public string? Location { get; set; }
    
    [StringLength(64)]
    public string? DeviceFingerprint { get; set; }
    
    /// <summary>
    /// Whether the login was successful
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Whether MFA was challenged
    /// </summary>
    public bool MfaChallenged { get; set; }
    
    /// <summary>
    /// Whether MFA was successfully completed
    /// </summary>
    public bool MfaSuccess { get; set; }
    
    /// <summary>
    /// Whether this was from a trusted device
    /// </summary>
    public bool TrustedDevice { get; set; }
    
    /// <summary>
    /// Risk score (0-100) calculated for this attempt
    /// </summary>
    public int RiskScore { get; set; }
    
    /// <summary>
    /// Reason for failure (if applicable)
    /// </summary>
    [StringLength(200)]
    public string? FailureReason { get; set; }
    
    [Required]
    public DateTime AttemptedAt { get; set; }
    
    // Geolocation fields for advanced risk scoring
    [StringLength(100)]
    public string? Country { get; set; }
    
    [StringLength(10)]
    public string? CountryCode { get; set; }
    
    [StringLength(100)]
    public string? Region { get; set; }
    
    [StringLength(100)]
    public string? City { get; set; }
    
    public double? Latitude { get; set; }
    
    public double? Longitude { get; set; }
    
    [StringLength(50)]
    public string? Timezone { get; set; }
    
    /// <summary>
    /// Hour of day (0-23) for pattern analysis
    /// </summary>
    public int? HourOfDay { get; set; }
    
    /// <summary>
    /// Day of week (0-6, Sunday=0) for pattern analysis
    /// </summary>
    public int? DayOfWeek { get; set; }
    
    // Navigation property
    public User User { get; set; } = null!;
}
