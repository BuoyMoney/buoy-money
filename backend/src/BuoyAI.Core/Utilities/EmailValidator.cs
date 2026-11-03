namespace BuoyAI.Core.Utilities;

/// <summary>
/// Provides email validation utilities for consistent validation across the application
/// </summary>
public static class EmailValidator
{
  /// <summary>
  /// Validates if a string is a properly formatted email address
  /// </summary>
  /// <param name="email">The email address to validate</param>
  /// <returns>True if the email is valid, false otherwise</returns>
  public static bool IsValidEmail(string email)
  {
    if (string.IsNullOrEmpty(email))
    {
      return false;
    }

    try
    {
      var emailAddress = new System.Net.Mail.MailAddress(email);
      return emailAddress.Address == email;
    }
    catch
    {
      return false;
    }
  }
}
