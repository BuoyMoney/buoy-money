namespace BuoyAI.Core.Configuration;

/// <summary>
/// Configuration settings for WebAuthn (passkey) functionality
/// </summary>
public class WebAuthnSettings
{
    /// <summary>
    /// The relying party identifier (domain)
    /// </summary>
    public string RelyingPartyId { get; set; } = "localhost";

    /// <summary>
    /// The human-readable relying party name
    /// </summary>
    public string RelyingPartyName { get; set; } = Brand.DefaultName;

    /// <summary>
    /// The expected origin for WebAuthn ceremonies
    /// </summary>
    public string Origin { get; set; } = "http://localhost:3000";

    /// <summary>
    /// Other origins a ceremony may come from, besides <see cref="Origin"/>: e.g. an Android app's
    /// "android:apk-key-hash:..." once its assetlinks.json is published. Exact matches only.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = new();

    /// <summary>True when clientDataJSON's origin is one this site accepts (exact match).</summary>
    public bool IsAllowedOrigin(string? origin) =>
        !string.IsNullOrEmpty(origin)
        && (string.Equals(origin, Origin.TrimEnd('/'), StringComparison.Ordinal)
            || AllowedOrigins.Any(o => string.Equals(origin, o.TrimEnd('/'), StringComparison.Ordinal)));

    /// <summary>True when the authenticator must report user verification (Face ID, fingerprint, PIN).</summary>
    public bool RequiresUserVerification =>
        string.Equals(UserVerification, "required", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Attestation conveyance preference (none, indirect, direct)
    /// </summary>
    public string AttestationConveyancePreference { get; set; } = "none";

    /// <summary>
    /// Authenticator attachment preference
    /// - "platform": Platform authenticators (Touch ID, Face ID, Windows Hello) - Higher security
    /// - "cross-platform": External authenticators (USB keys, mobile devices) - Better compatibility
    /// </summary>
    public string AuthenticatorAttachment { get; set; } = "platform";

    /// <summary>
    /// Whether to require resident keys (discoverable credentials)
    /// </summary>
    public bool RequireResidentKey { get; set; } = true;

    /// <summary>
    /// User verification requirement (required, preferred, discouraged)
    /// </summary>
    public string UserVerification { get; set; } = "preferred";

    /// <summary>
    /// Timeout for WebAuthn ceremonies in milliseconds
    /// </summary>
    public int Timeout { get; set; } = 300000; // 5 minutes
}
