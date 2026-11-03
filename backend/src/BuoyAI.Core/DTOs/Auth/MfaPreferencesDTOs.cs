namespace BuoyAI.Core.DTOs.Auth;

/// <summary>
/// User's MFA preferences response
/// </summary>
public class UserMfaPreferencesDto
{
    public string PrimaryMethod { get; set; } = "Email";
    public string? FallbackMethod { get; set; }
    public bool NotifyOnNewDevice { get; set; } = true;
    public bool NotifyOnSuspiciousLogin { get; set; } = true;
    public List<string> AvailableMethods { get; set; } = new();
}

/// <summary>
/// Request to update MFA preferences
/// </summary>
public class UpdateMfaPreferencesDto
{
    public string PrimaryMethod { get; set; } = "Email";
    public string? FallbackMethod { get; set; }
    public bool NotifyOnNewDevice { get; set; } = true;
    public bool NotifyOnSuspiciousLogin { get; set; } = true;
}
