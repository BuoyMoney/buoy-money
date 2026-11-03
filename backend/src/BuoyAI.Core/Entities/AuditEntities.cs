using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Entity for tracking data retention audit operations
/// </summary>
public class DataRetentionAuditLog
{
    public Guid Id { get; set; }
    
    [Required]
    public DateTime ExecutedAt { get; set; }
    
    [Required]
    [MaxLength(100)]
    public string OperationType { get; set; } = string.Empty;
    
    public int? UserId { get; set; }
    
    [MaxLength(100)]
    public string? RequestSource { get; set; }
    
    [MaxLength(100)]
    public string? DeletionType { get; set; }
    
    public string Summary { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(100)]
    public string ExecutedBy { get; set; } = string.Empty;
    
    [Required]
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Entity for tracking security audit logs
/// </summary>
public class SecurityAuditLog
{
    public int Id { get; set; }
    
    [Required]
    public int? UserId { get; set; }
    
    [Required]
    [MaxLength(100)]
    public string EventType { get; set; } = string.Empty;
    
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;
    
    [MaxLength(45)]
    public string? IpAddress { get; set; }
    
    [MaxLength(500)]
    public string? UserAgent { get; set; }
    
    public string? AdditionalData { get; set; }
    
    [Required]
    public DateTime CreatedAt { get; set; }
    
    // Navigation property
    public User? User { get; set; }
}

/// <summary>
/// Entity for tracking error logs
/// </summary>
public class ErrorLog
{
    public int Id { get; set; }
    
    public int? UserId { get; set; }
    
    [Required]
    [MaxLength(100)]
    public string ErrorType { get; set; } = string.Empty;
    
    [Required]
    public string Message { get; set; } = string.Empty;
    
    public string? StackTrace { get; set; }
    
    [MaxLength(200)]
    public string? Source { get; set; }
    
    [MaxLength(45)]
    public string? IpAddress { get; set; }
    
    public string? AdditionalData { get; set; }
    
    [Required]
    public DateTime CreatedAt { get; set; }
    
    // Navigation property
    public User? User { get; set; }
}
