using BuoyAI.Core.Entitlements;
using Microsoft.Extensions.Configuration;

namespace BuoyAI.Infrastructure.Services;

/// <summary>Reads the configurable parts of <see cref="AiLimits"/>.</summary>
public static class AiLimitsConfiguration
{
    /// <summary>
    /// Standard and Premium daily request counts honour <c>Gemini:RateLimiting:*</c>.
    /// Everything else is the built-in default.
    /// </summary>
    public static AiLimits Load(IConfiguration configuration)
    {
        var defaults = AiLimits.Default;
        return new AiLimits
        {
            Standard = defaults.Standard with
            {
                DailyRequests = configuration.GetValue(
                    "Gemini:RateLimiting:StandardUserRequestsPerDay", defaults.Standard.DailyRequests)
            },
            Premium = defaults.Premium with
            {
                DailyRequests = configuration.GetValue(
                    "Gemini:RateLimiting:PremiumUserRequestsPerDay", defaults.Premium.DailyRequests)
            },
            Admin = defaults.Admin,
        };
    }
}
