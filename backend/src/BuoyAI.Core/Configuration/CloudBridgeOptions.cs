namespace BuoyAI.Core.Configuration;

/// <summary>
/// Settings for the BuoyAI Cloud bank feed (section <c>CloudBridge</c>). Off unless <see cref="Url"/> is set, so the
/// hosted service and a self-hosted server that does not use the feed behave exactly as before.
/// </summary>
public class CloudBridgeOptions
{
  public const string SectionName = "CloudBridge";

  /// <summary>Base address of the Cloud bridge. Empty means the feed is not offered.</summary>
  public string Url { get; set; } = string.Empty;

  /// <summary>
  /// Offer the Cloud bank feed as a bank provider. <see cref="Url"/> alone only lets this server pair with the Cloud (for
  /// the free push relay); the feed needs this as well, because it is a paid Cloud plan and connecting a bank fails without it.
  /// </summary>
  public bool BankFeedEnabled { get; set; }

  /// <summary>
  /// The hosted page where the owner approves this server ("Link with BuoyAI Cloud"). The server sends the admin's
  /// browser there with a one-time state and its own callback address.
  /// </summary>
  public string LinkUrl { get; set; } = "https://buoy-ai.com/app/cloud/link";

  /// <summary>Reported to the Cloud in every call. Empty means the build version.</summary>
  public string ServerVersion { get; set; } = string.Empty;

  /// <summary>Least minutes between sync attempts on one bank connection. The Cloud also caps pulls itself.</summary>
  public int MinSyncIntervalMinutes { get; set; } = 30;

  public int JitterMinutes { get; set; } = 10;

  /// <summary>Most batches pulled for one connection in one sync, so a huge first load cannot hold the loop forever.</summary>
  public int MaxBatchesPerSync { get; set; } = 40;

  public int RequestTimeoutSeconds { get; set; } = 60;
}
