using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Represents a time-limited two-factor authentication code
/// </summary>
public class TwoFactorCode
{
    public int Id { get; set; }
    
    [Required]
    public int UserId { get; set; }
    
    [Required]
    [StringLength(6)]
    public string Code { get; set; } = string.Empty;
    
    [Required]
    [StringLength(50)]
    public string Purpose { get; set; } = string.Empty; // "plaid_connection", "settings_change", "login_verification"
    
    [Required]
    public DateTime ExpiresAt { get; set; }
    
    public bool IsUsed { get; set; }

    public DateTime? UsedAt { get; set; }

    // Number of incorrect verification attempts against this code. The code is
    // invalidated after a small number of wrong guesses to prevent brute-forcing.
    public int Attempts { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; }
    
    [StringLength(45)] // IPv4/IPv6 address
    public string? IpAddress { get; set; }
    
    [StringLength(500)]
    public string? UserAgent { get; set; }
    
    // Navigation property
    public User User { get; set; } = null!;
    
    /// <summary>
    /// Checks if the code is still valid (not expired and not used)
    /// </summary>
    public bool IsValid => !IsUsed && DateTime.UtcNow <= ExpiresAt;
}
