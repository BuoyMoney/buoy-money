using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The push pipeline: preference gating → quiet-hours deferral → FCM send → invalid-token
/// pruning. Hooked into NotificationService.CreateNotificationAsync so every in-app
/// notification automatically gets a push copy; the bell is the source of truth and push
/// is best-effort on top (SendToUserAsync never throws).
/// </summary>
public class PushNotificationService : IPushNotificationService
{
  private readonly BuoyAIDbContext _context;
  private readonly IFcmSender _fcmSender;
  private readonly IPushRelaySender? _relaySender;
  private readonly ILogger<PushNotificationService> _logger;

  /// <summary>
  /// Maps in-app notification Type strings to the preference category that gates them.
  /// Types not listed here are gated by the master PushEnabled toggle only — new
  /// notification types push by default rather than being silently dropped.
  /// </summary>
  private static readonly Dictionary<string, Func<UserPushPreferences, bool>> CategoryForType = new()
  {
    ["plaid_update_required"] = p => p.ConnectionIssues,
    ["plaid_expiration_warning"] = p => p.ConnectionIssues,
    ["plaid_permission_revoked"] = p => p.ConnectionIssues,
    ["plaid_institution_down"] = p => p.ConnectionIssues,
    ["plaid_reconnected"] = p => p.ConnectionIssues,
    ["budget_alert"] = p => p.BudgetAlerts,
    ["bill_reminder"] = p => p.BillReminders,
    ["subscription_detection"] = p => p.SubscriptionDetection,
    ["transaction_subcategory_needed"] = p => p.TransactionReview,
    ["receipt_split_ready"] = p => p.TransactionReview,
  };

  public PushNotificationService(
    BuoyAIDbContext context,
    IFcmSender fcmSender,
    ILogger<PushNotificationService> logger,
    IPushRelaySender? relaySender = null)
  {
    _context = context;
    _fcmSender = fcmSender;
    _relaySender = relaySender;
    _logger = logger;
  }

  /// <summary>Push can go out through Firebase (hosted) or the relay (self-host).</summary>
  private bool AnySenderEnabled => _fcmSender.IsEnabled || _relaySender?.IsEnabled == true;

  public async Task SendToUserAsync(int userId, string notificationType, string title, string body,
    string? actionUrl, CancellationToken cancellationToken = default)
  {
    try
    {
      if (!AnySenderEnabled)
      {
        return;
      }

      var preferences = await _context.UserPushPreferences
        .AsNoTracking()
        .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken)
        ?? new UserPushPreferences { UserId = userId };

      if (!preferences.PushEnabled)
      {
        return;
      }

      if (CategoryForType.TryGetValue(notificationType, out var categoryEnabled) && !categoryEnabled(preferences))
      {
        return;
      }

      var hasDevices = await _context.UserDeviceTokens
        .AnyAsync(t => t.UserId == userId, cancellationToken);
      if (!hasDevices)
      {
        return;
      }

      var data = BuildData(notificationType, actionUrl);

      if (IsInQuietHours(preferences, DateTime.UtcNow))
      {
        _context.QueuedPushNotifications.Add(new QueuedPushNotification
        {
          UserId = userId,
          Title = title,
          Body = body,
          DataJson = JsonSerializer.Serialize(data),
          DeliverAfter = NextQuietHoursEnd(preferences, DateTime.UtcNow)
        });
        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Queued push for user {UserId} until quiet hours end", userId);
        return;
      }

      await SendToDevicesAsync(userId, title, body, data, cancellationToken);
    }
    catch (Exception ex)
    {
      // Push is a bonus channel — never let it break notification creation.
      _logger.LogError(ex, "Failed to send push notification to user {UserId}", userId);
    }
  }

  public async Task RegisterDeviceAsync(int userId, RegisterDeviceRequest request)
  {
    var platform = request.Platform.ToLowerInvariant();

    if (request.TimeZoneOffsetMinutes.HasValue)
    {
      try
      {
        await UpsertTimeZoneOffsetAsync(userId, request.TimeZoneOffsetMinutes.Value, request.TimeZoneId);
        await _context.SaveChangesAsync();
      }
      catch (DbUpdateException ex) when (IsUniqueViolation(ex))
      {
        // The SAME first-launch race as the device token below, on a different index
        // (ux_user_push_preferences_user_id). This call sat outside the guard, so the
        // race was only ever handled for one of the two rows this endpoint writes:
        // both requests saw no preferences row and both INSERTed, and the loser 500'd
        // the whole registration even though the token half would have succeeded.
        _context.ChangeTracker.Clear();
        await UpsertTimeZoneOffsetAsync(userId, request.TimeZoneOffsetMinutes.Value, request.TimeZoneId);
        await _context.SaveChangesAsync();
      }
    }

    try
    {
      await UpsertDeviceTokenAsync(userId, request.Token, platform, request.PublicKey);
    }
    catch (DbUpdateException ex) when (IsUniqueViolation(ex))
    {
      // Concurrent registration race: the app registers on login AND on FCM token
      // refresh, which fire near-simultaneously on first launch, so two requests both
      // saw "no row" and both INSERTed the same token — the loser hits the unique index
      // (ux_user_device_tokens_token). The row now exists; drop our failed insert and
      // re-apply as an update so registration is idempotent instead of a 500.
      _context.ChangeTracker.Clear();
      await UpsertDeviceTokenAsync(userId, request.Token, platform, request.PublicKey);
    }
  }

  private async Task UpsertDeviceTokenAsync(int userId, string token, string platform, string? publicKey)
  {
    var existing = await _context.UserDeviceTokens
      .FirstOrDefaultAsync(t => t.Token == token);

    if (existing != null)
    {
      // A token identifies one install; if another user logs in on the same device the
      // token re-parents to them so pushes follow the active account.
      existing.UserId = userId;
      existing.Platform = platform;
      existing.PublicKey = platform == "relay" ? publicKey : null;
      existing.LastSeenAt = DateTime.UtcNow;
    }
    else
    {
      _context.UserDeviceTokens.Add(new UserDeviceToken
      {
        UserId = userId,
        Token = token,
        Platform = platform,
        PublicKey = platform == "relay" ? publicKey : null
      });
    }

    await _context.SaveChangesAsync();
  }

  /// <summary>
  /// True when the exception chain carries a Postgres unique-constraint violation
  /// (SQLSTATE 23505). Read from the provider exception's SqlState via reflection so this
  /// assembly takes no direct Npgsql dependency.
  /// </summary>
  private static bool IsUniqueViolation(Exception exception)
  {
    for (Exception? ex = exception; ex != null; ex = ex.InnerException)
    {
      if (ex.GetType().GetProperty("SqlState")?.GetValue(ex) as string == "23505")
      {
        return true;
      }
    }
    return false;
  }

  public async Task<bool> UnregisterDeviceAsync(int userId, string token)
  {
    var existing = await _context.UserDeviceTokens
      .FirstOrDefaultAsync(t => t.Token == token && t.UserId == userId);
    if (existing == null)
    {
      return false;
    }

    _context.UserDeviceTokens.Remove(existing);
    await _context.SaveChangesAsync();
    return true;
  }

  public async Task<PushPreferencesDto> GetPreferencesAsync(int userId)
  {
    var preferences = await _context.UserPushPreferences
      .AsNoTracking()
      .FirstOrDefaultAsync(p => p.UserId == userId)
      ?? new UserPushPreferences { UserId = userId };

    var deviceCount = await _context.UserDeviceTokens.CountAsync(t => t.UserId == userId);
    return ToDto(preferences, deviceCount);
  }

  public async Task<PushPreferencesDto> UpdatePreferencesAsync(int userId, UpdatePushPreferencesRequest request)
  {
    try
    {
      return await ApplyPreferencesAsync(userId, request);
    }
    catch (DbUpdateException ex) when (IsUniqueViolation(ex))
    {
      // Same first-launch race as device registration: a concurrent writer created the
      // preferences row between our read and our insert. The row exists now, so re-read
      // and apply as an update rather than 500ing.
      _context.ChangeTracker.Clear();
      return await ApplyPreferencesAsync(userId, request);
    }
  }

  private async Task<PushPreferencesDto> ApplyPreferencesAsync(int userId, UpdatePushPreferencesRequest request)
  {
    var preferences = await _context.UserPushPreferences
      .FirstOrDefaultAsync(p => p.UserId == userId);

    if (preferences == null)
    {
      preferences = new UserPushPreferences { UserId = userId };
      _context.UserPushPreferences.Add(preferences);
    }

    preferences.PushEnabled = request.PushEnabled;
    preferences.ConnectionIssues = request.ConnectionIssues;
    preferences.BudgetAlerts = request.BudgetAlerts;
    preferences.BillReminders = request.BillReminders;
    preferences.SubscriptionDetection = request.SubscriptionDetection;
    preferences.TransactionReview = request.TransactionReview;
    preferences.QuietHoursEnabled = request.QuietHoursEnabled;
    preferences.QuietHoursStartMinutes = request.QuietHoursStartMinutes;
    preferences.QuietHoursEndMinutes = request.QuietHoursEndMinutes;
    if (request.TimeZoneOffsetMinutes.HasValue)
    {
      preferences.TimeZoneOffsetMinutes = request.TimeZoneOffsetMinutes.Value;
    }
    if (request.TimeZoneId != null)
    {
      preferences.TimeZoneId = request.TimeZoneId;
    }
    preferences.UpdatedAt = DateTime.UtcNow;

    await _context.SaveChangesAsync();

    var deviceCount = await _context.UserDeviceTokens.CountAsync(t => t.UserId == userId);
    return ToDto(preferences, deviceCount);
  }

  public async Task<int> DeliverDueQueuedAsync(CancellationToken cancellationToken = default)
  {
    if (!AnySenderEnabled)
    {
      return 0;
    }

    var due = await _context.QueuedPushNotifications
      .Where(q => q.DeliverAfter <= DateTime.UtcNow)
      .OrderBy(q => q.Id)
      .Take(200)
      .ToListAsync(cancellationToken);

    var sent = 0;
    foreach (var queued in due)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        // Master toggle is re-checked at delivery time (the user may have turned push off
        // overnight); the category gate already passed at queue time.
        var stillEnabled = await _context.UserPushPreferences
          .AsNoTracking()
          .Where(p => p.UserId == queued.UserId)
          .Select(p => (bool?)p.PushEnabled)
          .FirstOrDefaultAsync(cancellationToken) ?? true;

        if (stillEnabled)
        {
          var data = JsonSerializer.Deserialize<Dictionary<string, string>>(queued.DataJson)
            ?? new Dictionary<string, string>();
          await SendToDevicesAsync(queued.UserId, queued.Title, queued.Body, data, cancellationToken);
          sent++;
        }

        _context.QueuedPushNotifications.Remove(queued);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        queued.Attempts++;
        if (queued.Attempts >= 3)
        {
          _logger.LogWarning(ex, "Dropping queued push {QueuedId} after {Attempts} attempts", queued.Id, queued.Attempts);
          _context.QueuedPushNotifications.Remove(queued);
        }
        else
        {
          _logger.LogWarning(ex, "Queued push {QueuedId} failed (attempt {Attempts}), will retry", queued.Id, queued.Attempts);
        }
      }
    }

    if (due.Count > 0)
    {
      await _context.SaveChangesAsync(cancellationToken);
    }

    return sent;
  }

  public async Task<int> PruneStaleTokensAsync(CancellationToken cancellationToken = default)
  {
    // 270 days is FCM's own staleness horizon (docs: "manage FCM registration tokens").
    // Fetch-then-remove (not ExecuteDelete) so the EF InMemory test provider works too.
    var cutoff = DateTime.UtcNow.AddDays(-270);
    var stale = await _context.UserDeviceTokens
      .Where(t => t.LastSeenAt < cutoff)
      .ToListAsync(cancellationToken);

    if (stale.Count > 0)
    {
      _context.UserDeviceTokens.RemoveRange(stale);
      await _context.SaveChangesAsync(cancellationToken);
      _logger.LogInformation("Pruned {Count} stale device tokens", stale.Count);
    }
    return stale.Count;
  }

  private async Task SendToDevicesAsync(int userId, string title, string body,
    IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken)
  {
    var devices = await _context.UserDeviceTokens
      .Where(t => t.UserId == userId)
      .Select(t => new { t.Token, t.Platform, t.PublicKey })
      .ToListAsync(cancellationToken);

    if (devices.Count == 0)
    {
      return;
    }

    var relayDevices = devices
      .Where(d => d.Platform == "relay" && !string.IsNullOrEmpty(d.PublicKey))
      .Select(d => new RelayDevice(d.Token, d.PublicKey!))
      .ToList();
    var tokens = devices.Where(d => d.Platform != "relay").Select(d => d.Token).ToList();

    var success = 0;
    var failed = 0;
    var invalidTokens = new List<string>();

    if (tokens.Count > 0 && _fcmSender.IsEnabled)
    {
      var result = await _fcmSender.SendAsync(tokens, title, body, data, cancellationToken);
      success += result.SuccessCount;
      failed += result.FailureCount;
      invalidTokens.AddRange(result.InvalidTokens);
    }

    if (relayDevices.Count > 0 && _relaySender?.IsEnabled == true)
    {
      var result = await _relaySender.SendAsync(relayDevices, title, body, data, cancellationToken);
      success += result.SuccessCount;
      failed += result.FailureCount;
      invalidTokens.AddRange(result.InvalidTokens);
    }

    if (invalidTokens.Count > 0)
    {
      var invalid = await _context.UserDeviceTokens
        .Where(t => invalidTokens.Contains(t.Token))
        .ToListAsync(cancellationToken);
      _context.UserDeviceTokens.RemoveRange(invalid);
      await _context.SaveChangesAsync(cancellationToken);
      _logger.LogInformation("Pruned {Count} invalid device tokens for user {UserId}",
        invalid.Count, userId);
    }

    _logger.LogInformation(
      "Push to user {UserId}: {Success} delivered, {Failed} failed across {Devices} devices",
      userId, success, failed, devices.Count);
  }

  private static Dictionary<string, string> BuildData(string notificationType, string? actionUrl)
  {
    var data = new Dictionary<string, string>
    {
      ["type"] = notificationType,
      // The client falls back to the notifications page when no deeper route is attached.
      ["route"] = string.IsNullOrWhiteSpace(actionUrl) ? "/notifications" : actionUrl
    };
    return data;
  }

  /// <summary>
  /// Quiet hours are minutes-from-midnight in the user's local time; a window whose start
  /// is after its end wraps past midnight (22:00 → 07:00). Internal-visible for unit tests.
  /// </summary>
  internal static bool IsInQuietHours(UserPushPreferences preferences, DateTime utcNow)
  {
    if (!preferences.QuietHoursEnabled)
    {
      return false;
    }

    var localMinutes = LocalMinutesOfDay(preferences, utcNow);
    var start = preferences.QuietHoursStartMinutes;
    var end = preferences.QuietHoursEndMinutes;

    if (start == end)
    {
      return false; // Zero-length window.
    }

    return start < end
      ? localMinutes >= start && localMinutes < end
      : localMinutes >= start || localMinutes < end; // Wraps midnight.
  }

  /// <summary>The next UTC instant the quiet-hours window ends. Internal-visible for unit tests.</summary>
  internal static DateTime NextQuietHoursEnd(UserPushPreferences preferences, DateTime utcNow)
  {
    var localMinutes = LocalMinutesOfDay(preferences, utcNow);
    var end = preferences.QuietHoursEndMinutes;

    // Minutes until the end mark next comes around on the user's local clock.
    var minutesUntilEnd = end - localMinutes;
    if (minutesUntilEnd <= 0)
    {
      minutesUntilEnd += 24 * 60;
    }

    return utcNow.AddMinutes(minutesUntilEnd);
  }

  private static int LocalMinutesOfDay(UserPushPreferences preferences, DateTime utcNow)
  {
    // Prefer the IANA zone: it follows DST the moment it happens. The stored fixed
    // offset only self-heals on the next app start, which is exactly the DST-drift
    // window this fixes (#941). Unknown ids (bad client data, trimmed ICU) fall back.
    if (!string.IsNullOrEmpty(preferences.TimeZoneId))
    {
      try
      {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(preferences.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        return local.Hour * 60 + local.Minute;
      }
      catch (TimeZoneNotFoundException)
      {
        // fall through to the fixed offset
      }
      catch (InvalidTimeZoneException)
      {
        // fall through to the fixed offset
      }
    }
    var fallback = utcNow.AddMinutes(preferences.TimeZoneOffsetMinutes);
    return fallback.Hour * 60 + fallback.Minute;
  }

  private async Task UpsertTimeZoneOffsetAsync(int userId, int offsetMinutes, string? timeZoneId = null)
  {
    var preferences = await _context.UserPushPreferences
      .FirstOrDefaultAsync(p => p.UserId == userId);
    if (preferences == null)
    {
      _context.UserPushPreferences.Add(new UserPushPreferences
      {
        UserId = userId,
        TimeZoneOffsetMinutes = offsetMinutes,
        TimeZoneId = timeZoneId
      });
    }
    else if (preferences.TimeZoneOffsetMinutes != offsetMinutes
             || (timeZoneId != null && preferences.TimeZoneId != timeZoneId))
    {
      preferences.TimeZoneOffsetMinutes = offsetMinutes;
      // Never null out a known zone id just because one request omitted it.
      if (timeZoneId != null)
      {
        preferences.TimeZoneId = timeZoneId;
      }
      preferences.UpdatedAt = DateTime.UtcNow;
    }
  }

  private static PushPreferencesDto ToDto(UserPushPreferences preferences, int deviceCount) => new()
  {
    PushEnabled = preferences.PushEnabled,
    ConnectionIssues = preferences.ConnectionIssues,
    BudgetAlerts = preferences.BudgetAlerts,
    BillReminders = preferences.BillReminders,
    SubscriptionDetection = preferences.SubscriptionDetection,
    TransactionReview = preferences.TransactionReview,
    QuietHoursEnabled = preferences.QuietHoursEnabled,
    QuietHoursStartMinutes = preferences.QuietHoursStartMinutes,
    QuietHoursEndMinutes = preferences.QuietHoursEndMinutes,
    RegisteredDeviceCount = deviceCount
  };
}
