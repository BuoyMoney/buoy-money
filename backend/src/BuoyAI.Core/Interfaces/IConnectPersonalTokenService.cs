using BuoyAI.Core.DTOs.OAuth;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Personal access tokens for BuoyAI Connect: long-lived, revocable Connect tokens a user makes for a tool
/// that cannot run the OAuth flow (Open WebUI, a script). See <see cref="Entities.ConnectPersonalToken"/>.
/// </summary>
public interface IConnectPersonalTokenService
{
  /// <summary>The client_id every personal token carries, so <c>/mcp</c> knows to check revocation.</summary>
  const string ClientId = "personal-token";

  Task<IReadOnlyList<ConnectPersonalTokenDto>> ListAsync(int userId, CancellationToken ct);

  /// <summary>Creates a token. The returned value is shown to the user once and never stored.</summary>
  Task<CreatedConnectPersonalTokenDto> CreateAsync(int userId, string name, CancellationToken ct);

  /// <summary>Revokes one of the user's tokens. False when it is not theirs or does not exist.</summary>
  Task<bool> RevokeAsync(int userId, Guid id, CancellationToken ct);

  /// <summary>True when the token with this id belongs to the user and is unrevoked and unexpired. Records the use.</summary>
  Task<bool> ValidateAsync(Guid id, int userId, CancellationToken ct);
}
