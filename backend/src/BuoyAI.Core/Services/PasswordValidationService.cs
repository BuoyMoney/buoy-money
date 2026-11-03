using System.Text.RegularExpressions;

namespace BuoyAI.Core.Services;

/// <summary>
/// Service for validating password strength and security requirements
/// </summary>
public class PasswordValidationService
{
    private static readonly string[] CommonWeakPasswords = new[]
    {
        "password", "password123", "123456", "123456789", "qwerty", "abc123",
        "password1", "admin", "letmein", "welcome", "monkey", "1234567890"
    };

    /// <summary>
    /// Validates password against security requirements
    /// </summary>
    public static PasswordValidationResult ValidatePassword(string password)
    {
        var result = new PasswordValidationResult();
        
        if (string.IsNullOrEmpty(password))
        {
            result.Errors.Add("Password is required");
            return result;
        }

        // Length requirement (minimum 8 characters)
        if (password.Length < 8)
        {
            result.Errors.Add("Password must be at least 8 characters long");
        }

        // Uppercase letter requirement
        if (!Regex.IsMatch(password, @"[A-Z]"))
        {
            result.Errors.Add("Password must contain at least one uppercase letter");
        }

        // Lowercase letter requirement
        if (!Regex.IsMatch(password, @"[a-z]"))
        {
            result.Errors.Add("Password must contain at least one lowercase letter");
        }

        // Number requirement
        if (!Regex.IsMatch(password, @"[0-9]"))
        {
            result.Errors.Add("Password must contain at least one number");
        }

        // Special character requirement
        if (!Regex.IsMatch(password, @"[!@#$%^&*()_+\-=\[\]{};':""\\|,.<>\/?]"))
        {
            result.Errors.Add("Password must contain at least one special character");
        }

        // Common weak password check
        if (CommonWeakPasswords.Contains(password.ToLower()))
        {
            result.Errors.Add("Password is too common and easily guessable");
        }

        // Check for repetitive patterns
        if (HasRepetitivePattern(password))
        {
            result.Errors.Add("Password contains repetitive patterns");
        }

        result.IsValid = result.Errors.Count == 0;
        result.Strength = CalculatePasswordStrength(password);

        return result;
    }

    /// <summary>
    /// Calculates password strength score
    /// </summary>
    private static PasswordStrength CalculatePasswordStrength(string password)
    {
        var score = 0;
        
        // Length scoring
        if (password.Length >= 8) score += 1;
        if (password.Length >= 12) score += 1;
        
        // Character type scoring
        if (Regex.IsMatch(password, @"[A-Z]")) score += 1;
        if (Regex.IsMatch(password, @"[a-z]")) score += 1;
        if (Regex.IsMatch(password, @"[0-9]")) score += 1;
        if (Regex.IsMatch(password, @"[!@#$%^&*()_+\-=\[\]{};':""\\|,.<>\/?]")) score += 1;
        
        // Complexity bonus
        var uniqueChars = password.Distinct().Count();
        if (uniqueChars >= password.Length * 0.7) score += 1;

        return score switch
        {
            >= 6 => PasswordStrength.Strong,
            >= 4 => PasswordStrength.Medium,
            _ => PasswordStrength.Weak
        };
    }

    /// <summary>
    /// Checks for repetitive patterns in password
    /// </summary>
    private static bool HasRepetitivePattern(string password)
    {
        // Check for repeated characters (3 or more in a row)
        for (int i = 0; i < password.Length - 2; i++)
        {
            if (password[i] == password[i + 1] && password[i] == password[i + 2])
            {
                return true;
            }
        }

        // Check for sequential patterns (123, abc, etc.)
        for (int i = 0; i < password.Length - 2; i++)
        {
            var char1 = password[i];
            var char2 = password[i + 1];
            var char3 = password[i + 2];

            if (char2 == char1 + 1 && char3 == char2 + 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a password needs to be updated based on age and security policies
    /// </summary>
    public static bool RequiresPasswordUpdate(DateTime? lastPasswordChange, int maxAgeInDays = 365)
    {
        if (!lastPasswordChange.HasValue)
        {
            return true; // No record of password change, require update
        }

        return DateTime.UtcNow.Subtract(lastPasswordChange.Value).TotalDays > maxAgeInDays;
    }
}

/// <summary>
/// Result of password validation
/// </summary>
public class PasswordValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public PasswordStrength Strength { get; set; } = PasswordStrength.Weak;
}

/// <summary>
/// Password strength levels
/// </summary>
public enum PasswordStrength
{
    Weak = 0,
    Medium = 1,
    Strong = 2
}
