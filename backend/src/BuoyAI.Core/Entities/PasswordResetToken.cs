using System;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Entity for managing password reset tokens
/// </summary>
public class PasswordResetToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    
    /// <summary>
    /// Check if token is valid (not used and not expired)
    /// </summary>
    public bool IsValid => 
        UsedAt == null && 
        DateTime.UtcNow < ExpiresAt;
}
