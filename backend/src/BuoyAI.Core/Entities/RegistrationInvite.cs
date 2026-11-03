namespace BuoyAI.Core.Entities;

/// <summary>
/// A one-time sign-up code an admin hands out on a self-hosted server whose registration is closed.
/// Only the SHA-256 hash is stored; the code is shown to the admin once, when it is created.
/// </summary>
public class RegistrationInvite
{
  public int Id { get; set; }
  public string CodeHash { get; set; } = string.Empty;
  public string? Note { get; set; }
  public int CreatedByUserId { get; set; }
  public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
  public DateTime ExpiresAt { get; set; }
  public int? UsedByUserId { get; set; }
  public DateTime? UsedAt { get; set; }
}

/// <summary>A server-wide setting an admin can change at runtime (self-host only).</summary>
public class InstanceSetting
{
  public string Key { get; set; } = string.Empty;
  public string Value { get; set; } = string.Empty;
  public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
