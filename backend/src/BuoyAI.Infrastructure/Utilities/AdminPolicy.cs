using Microsoft.Extensions.Configuration;

namespace BuoyAI.Infrastructure.Utilities;

/// <summary>
/// Admin tier is NOT purchasable. It is granted only to a fixed allowlist of operator
/// emails configured under "Admin:Emails". This is what unlocks the internal dev tools
/// (e.g. the dev timeline page) without letting any paying customer self-assign Admin.
/// </summary>
public static class AdminPolicy
{
    public static bool IsAdminEmail(IConfiguration configuration, string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var normalized = email.Trim().ToLowerInvariant();
        // Null-safe: GetSection can return null under mocked IConfiguration in tests.
        var allowed = configuration.GetSection("Admin:Emails")?.Get<string[]>();
        if (allowed == null)
        {
            return false;
        }

        return allowed.Any(e =>
            !string.IsNullOrWhiteSpace(e) &&
            string.Equals(e.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }
}
