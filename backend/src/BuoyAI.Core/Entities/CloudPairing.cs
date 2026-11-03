namespace BuoyAI.Core.Entities;

/// <summary>
/// This self-hosted server's pairing with a BuoyAI Cloud account (docs/self_host/bridge.md). One row at most
/// (<c>Id</c> is always 1). The credential and both private keys are encrypted at rest and never leave the
/// server: the Cloud holds only the matching public keys and a hash of the credential.
/// </summary>
public class CloudPairing
{
  public int Id { get; set; } = 1;

  /// <summary>Base address of the Cloud bridge this server paired with.</summary>
  public string CloudUrl { get; set; } = string.Empty;

  public Guid ServerId { get; set; }
  public string Name { get; set; } = string.Empty;

  /// <summary>The per-server credential (<c>bmk_...</c>), encrypted.</summary>
  public string CredentialCipher { get; set; } = string.Empty;

  /// <summary>PKCS#8 of the P-256 key that opens deliveries, encrypted.</summary>
  public string EncryptionKeyCipher { get; set; } = string.Empty;

  /// <summary>PKCS#8 of the P-256 key that signs requests, encrypted.</summary>
  public string SigningKeyCipher { get; set; } = string.Empty;

  public DateTime PairedAt { get; set; } = DateTime.UtcNow;
}
