using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// In-memory daily upload-quota tracker (issue #367). Keys usage by user + UTC date and
/// expires the entry at the next UTC midnight, so the quota naturally resets each day.
///
/// NOTE: in-memory means the counter is per-process and resets on restart. Production runs a
/// single backend container, so this enforces the quota in practice. If the backend is ever
/// scaled horizontally, swap the store for a distributed cache (the interface is unchanged).
/// </summary>
public class UploadQuotaService : IUploadQuotaService
{
  private readonly IMemoryCache _cache;

  // 50 MB per user per UTC day — mirrors AIService.FileUpload's DailyUploadQuotaBytes.
  public long DailyQuotaBytes => 50L * 1024 * 1024;

  public UploadQuotaService(IMemoryCache cache)
  {
    _cache = cache;
  }

  private static string Key(int userId) =>
    $"upload_quota_{userId}_{DateTime.UtcNow:yyyyMMdd}";

  public long GetUsedBytesToday(int userId) =>
    _cache.TryGetValue(Key(userId), out long used) ? used : 0;

  public long GetRemainingBytesToday(int userId) =>
    Math.Max(0, DailyQuotaBytes - GetUsedBytesToday(userId));

  public void RecordUsage(int userId, long bytes)
  {
    var key = Key(userId);
    var used = GetUsedBytesToday(userId) + bytes;
    // Expire just after the next UTC midnight so the counter resets daily.
    var expiresAt = DateTime.UtcNow.Date.AddDays(1).AddMinutes(1);
    _cache.Set(key, used, expiresAt - DateTime.UtcNow);
  }
}
