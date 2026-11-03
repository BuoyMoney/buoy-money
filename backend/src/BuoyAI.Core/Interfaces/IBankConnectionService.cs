using BuoyAI.Core.DTOs;

namespace BuoyAI.Core.Interfaces;

/// <summary>Provider-neutral view of a user's bank connections: list them, disconnect one.</summary>
public interface IBankConnectionService
{
    /// <summary>The connections the user can see (their own and their family's), every provider.</summary>
    Task<IReadOnlyList<BankConnectionSummaryDto>> ListAsync(int userId);

    /// <summary>
    /// Disconnects one of the user's own connections through the provider it came from.
    /// Returns false when there is no such connection or it is not the user's.
    /// </summary>
    Task<bool> DisconnectAsync(int userId, int connectionId);
}
