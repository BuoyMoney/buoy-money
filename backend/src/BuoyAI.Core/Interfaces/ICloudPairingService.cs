namespace BuoyAI.Core.Interfaces;

public record CloudPairingStatus(bool Configured, bool Paired, string? CloudUrl, Guid? ServerId, string? Name, DateTime? PairedAt, bool? AwaitingOwnerConfirmation);

/// <summary>Pairs this self-hosted server with a BuoyAI Cloud account so it can use the bank feed and the AI gateway.</summary>
public interface ICloudPairingService
{
  Task<CloudPairingStatus> GetStatusAsync(CancellationToken ct = default);

  /// <summary>
  /// Makes this server's two key pairs, spends the pairing code the owner made in the Cloud portal and stores the
  /// credential it gets back. Throws <c>DomainValidationException</c> with a message safe to show.
  /// </summary>
  Task<CloudPairingStatus> PairAsync(string code, string? name, CancellationToken ct = default);

  /// <summary>Forgets the pairing on this server. The owner revokes it in the Cloud portal to stop the Cloud side.</summary>
  Task UnpairAsync(CancellationToken ct = default);
}
