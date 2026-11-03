using BuoyAI.Core.DTOs.AI;

namespace BuoyAI.Core.Interfaces;

/// <summary>The numbers behind the AI usage screen, wherever the allowance lives (BuoyAI Cloud, or this server's own tracking).</summary>
public interface IAiUsageSummaryService
{
    Task<AiUsageSummary> GetAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns pay-as-you-go on (with a cap in micro-dollars) or off. Only meaningful when the allowance lives in BuoyAI Cloud.
    /// </summary>
    /// <exception cref="BuoyAI.Core.Exceptions.DomainValidationException">There is nothing to change on this server, or the cap is not allowed.</exception>
    Task<AiUsageSummary> SetPaygAsync(int userId, bool enabled, long capMicros, CancellationToken cancellationToken = default);
}
