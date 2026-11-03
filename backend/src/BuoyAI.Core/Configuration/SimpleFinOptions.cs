namespace BuoyAI.Core.Configuration;

/// <summary>
/// Settings for the SimpleFIN bank provider (section <c>SimpleFin</c>). SimpleFIN is a bridge the
/// user pays for directly (about $15 a year) and BuoyAI pays nothing for. The user makes a setup
/// token at the bridge and pastes it into BuoyAI.
/// </summary>
public class SimpleFinOptions
{
    public const string SectionName = "SimpleFin";

    /// <summary>
    /// Whether SimpleFIN is offered. Unset means on for a self-hosted server and off for the hosted
    /// service, so hosted behaves as before until someone turns it on.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>Where the user creates a setup token. Shown in the app.</summary>
    public string BridgeUrl { get; set; } = "https://bridge.simplefin.org/simplefin/create";

    /// <summary>
    /// Comma-separated domains BuoyAI will call. A host matches when it equals one of them or is a
    /// subdomain of one. A pasted token chooses the URL we POST to, so this list is what stops it
    /// pointing us at anything else.
    /// </summary>
    public string AllowedHosts { get; set; } = "simplefin.org";

    /// <summary>
    /// Least hours between sync attempts on one connection. SimpleFIN asks for 24 requests a day or
    /// fewer per token, and its data only changes about daily.
    /// </summary>
    public int MinSyncIntervalHours { get; set; } = 6;

    /// <summary>Extra minutes, up to this, spread per connection so they do not all call at once.</summary>
    public int JitterMinutes { get; set; } = 60;

    /// <summary>How far back the first sync after connecting asks for. Longer than 90 is sent as several requests.</summary>
    public int InitialImportDays { get; set; } = 90;

    /// <summary>Seconds before a call to the bridge is given up.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;
}
