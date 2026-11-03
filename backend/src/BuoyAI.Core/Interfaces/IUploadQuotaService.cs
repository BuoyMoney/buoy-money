namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Tracks and enforces the per-user daily AI file-upload quota (issue #367).
/// Backed by an in-memory cache that rolls over at UTC midnight.
/// </summary>
public interface IUploadQuotaService
{
  /// <summary>The total bytes a user may upload per UTC day.</summary>
  long DailyQuotaBytes { get; }

  /// <summary>Bytes the user has already uploaded today (UTC).</summary>
  long GetUsedBytesToday(int userId);

  /// <summary>Remaining bytes the user may upload today (never negative).</summary>
  long GetRemainingBytesToday(int userId);

  /// <summary>Record a successful upload of <paramref name="bytes"/> against today's quota.</summary>
  void RecordUsage(int userId, long bytes);
}
