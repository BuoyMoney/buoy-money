using System.Text;

namespace BuoyAI.Core.Configuration;

/// <summary>
/// The hosted app acting as the BuoyAI Cloud portal: it lists, links and revokes a user's self-hosted
/// servers by calling the Cloud pairing API with a short owner token it signs itself. Bound to the
/// "CloudPortal" section. Off until <see cref="OwnerJwtKey"/> is set.
/// </summary>
public class CloudPortalOptions
{
    public const string SectionName = "CloudPortal";

    /// <summary>
    /// Base address of the Cloud bridge, which serves the pairing API under <c>/v1/pairing</c>. On the
    /// production box that is the bridge's loopback door.
    /// </summary>
    public string PairingUrl { get; set; } = "http://127.0.0.1:5201";

    /// <summary>Must equal the bridge's <c>Owner__JwtKey</c> (at least 32 bytes). Never logged.</summary>
    public string OwnerJwtKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "buoy-cloud-portal";

    public string Audience { get; set; } = "buoy-cloud";

    public int TimeoutSeconds { get; set; } = 15;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PairingUrl)
        && !string.IsNullOrEmpty(OwnerJwtKey)
        && Encoding.UTF8.GetByteCount(OwnerJwtKey) >= 32;
}
