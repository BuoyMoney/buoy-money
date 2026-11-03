namespace BuoyAI.Core.Entities;

/// <summary>
/// Links a user to an account at an outside sign-in provider (OpenID Connect single sign-on on a
/// self-hosted server, for example Pocket ID). The provider's issuer plus its stable subject id
/// (<c>sub</c>) identify the person, so later sign-ins still match after they change their email
/// at the provider. The first sign-in links by verified email.
/// </summary>
public class UserExternalLogin
{
  public int Id { get; set; }

  public int UserId { get; set; }

  /// <summary>The provider's issuer URL, exactly as its ID tokens carry it in <c>iss</c>.</summary>
  public string Issuer { get; set; } = string.Empty;

  /// <summary>The provider's id for this person (<c>sub</c>).</summary>
  public string Subject { get; set; } = string.Empty;

  /// <summary>The email the provider last reported, for the admin's reference only. Never used to match.</summary>
  public string? Email { get; set; }

  public DateTime CreatedAt { get; set; }

  public DateTime? LastLoginAt { get; set; }
}
