namespace BuoyAI.Core.DTOs.Email;

public class TwoFactorEmailRequest
{
    public string Email { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
}

public class WelcomeEmailRequest
{
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
}

public class PasswordResetEmailRequest
{
    public string Email { get; set; } = string.Empty;
    public string ResetLink { get; set; } = string.Empty;
}

public class MfaUpgradeReminderRequest
{
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public int DaysLeft { get; set; }
}
