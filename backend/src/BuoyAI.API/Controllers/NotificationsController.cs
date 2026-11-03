using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.API.Controllers;

[Authorize]
public class NotificationsController : BaseController
{
    private readonly INotificationService _notificationService;
    private readonly IPushNotificationService _pushNotificationService;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        INotificationService notificationService,
        IPushNotificationService pushNotificationService,
        ILogger<NotificationsController> logger)
    {
        _notificationService = notificationService;
        _pushNotificationService = pushNotificationService;
        _logger = logger;
    }

    /// <summary>
    /// Register (or refresh) an FCM device token for push notifications.
    /// Called on login, app start, and FCM token rotation.
    /// </summary>
    [HttpPost("devices")]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request)
    {
        var platform = request.Platform.ToLowerInvariant();
        if (platform != "android" && platform != "ios" && platform != "relay")
        {
            return BadRequest("Platform must be 'android', 'ios' or 'relay'");
        }
        if (platform == "relay" &&
            HttpContext.RequestServices.GetService<BuoyAI.Infrastructure.Configuration.EditionInfo>()?.IsSelfHost != true)
        {
            return BadRequest("Relay push is only available on a self-hosted server");
        }
        if (platform == "relay" && !PushEnvelope.IsValidPublicKey(request.PublicKey))
        {
            return BadRequest("A relay device needs its public key (base64 of an uncompressed P-256 point)");
        }

        var userId = GetCurrentUserId();
        await _pushNotificationService.RegisterDeviceAsync(userId, request);
        _logger.LogInformation("Registered {Platform} push device for user {UserId}", platform, userId);
        return NoContent();
    }

    /// <summary>Whether relayed push can reach phones from this server, for the owner's setup checklist.</summary>
    public record RelayStatus(bool Configured, bool Ready);

    /// <summary>
    /// Self-host: whether the push relay is set (<c>PushRelay:Url</c>) and whether this server holds a credential the
    /// relay accepts. Hosted: both false (hosted sends through Firebase directly). Values only, no addresses or keys.
    /// </summary>
    [HttpGet("relay-status")]
    public async Task<IActionResult> GetRelayStatus([FromServices] IPushRelaySender relay, CancellationToken cancellationToken)
    {
        var selfHost = HttpContext.RequestServices.GetService<BuoyAI.Infrastructure.Configuration.EditionInfo>()?.IsSelfHost == true;
        if (!selfHost) return Ok(new RelayStatus(false, false));
        return Ok(new RelayStatus(relay.IsEnabled, await relay.IsReadyAsync(cancellationToken)));
    }

    /// <summary>
    /// Unregister a device token (called on logout so pushes stop following the account).
    /// </summary>
    [HttpDelete("devices")]
    public async Task<IActionResult> UnregisterDevice([FromBody] UnregisterDeviceRequest request)
    {
        var userId = GetCurrentUserId();
        var removed = await _pushNotificationService.UnregisterDeviceAsync(userId, request.Token);
        _logger.LogInformation("Unregister push device for user {UserId}: {Removed}", userId, removed);
        return NoContent();
    }

    /// <summary>
    /// Get the current user's push notification preferences (defaults if never saved).
    /// </summary>
    [HttpGet("push-preferences")]
    public async Task<ActionResult<PushPreferencesDto>> GetPushPreferences()
    {
        var userId = GetCurrentUserId();
        var preferences = await _pushNotificationService.GetPreferencesAsync(userId);
        return Ok(preferences);
    }

    /// <summary>
    /// Update the current user's push notification preferences (full-object PUT).
    /// </summary>
    [HttpPut("push-preferences")]
    public async Task<ActionResult<PushPreferencesDto>> UpdatePushPreferences(
        [FromBody] UpdatePushPreferencesRequest request)
    {
        var userId = GetCurrentUserId();
        var preferences = await _pushNotificationService.UpdatePreferencesAsync(userId, request);
        _logger.LogInformation("Updated push preferences for user {UserId}", userId);
        return Ok(preferences);
    }

    /// <summary>
    /// Get user's notifications with pagination
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<NotificationDto>>> GetNotifications(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = GetCurrentUserId();
        _logger.LogInformation("Getting notifications for user ID: {UserId}", userId);
        var notifications = await _notificationService.GetUserNotificationsAsync(userId, page, pageSize);
        _logger.LogInformation("Found {Count} notifications for user {UserId}", notifications.Count, userId);
        return Ok(notifications);
    }

    /// <summary>
    /// Get user's notifications with pagination and archiving support
    /// </summary>
    [HttpGet("paginated")]
    public async Task<ActionResult<object>> GetNotificationsPaginated(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool includeArchived = false)
    {
        var userId = GetCurrentUserId();
        _logger.LogInformation("Getting paginated notifications for user ID: {UserId}, page: {Page}, includeArchived: {IncludeArchived}",
            userId, page, includeArchived);

        var (notifications, hasMore) = await _notificationService.GetUserNotificationsPaginatedAsync(
            userId, page, pageSize, includeArchived);

        return Ok(new
        {
            notifications,
            hasMore,
            page,
            pageSize,
            includeArchived
        });
    }

    /// <summary>
    /// Get notification summary (count + recent notifications)
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<NotificationSummaryDto>> GetNotificationSummary()
    {
        var userId = GetCurrentUserId();
        var summary = await _notificationService.GetNotificationSummaryAsync(userId);
        return Ok(summary);
    }

    /// <summary>
    /// Get unread notification count
    /// </summary>
    [HttpGet("unread-count")]
    public async Task<ActionResult<int>> GetUnreadCount()
    {
        var userId = GetCurrentUserId();
        var count = await _notificationService.GetUnreadCountAsync(userId);
        return Ok(count);
    }

    /// <summary>
    /// Mark a specific notification as read
    /// </summary>
    [HttpPost("{id}/mark-read")]
    public async Task<IActionResult> MarkAsRead(int id)
    {
        var userId = GetCurrentUserId();
        var success = await _notificationService.MarkAsReadAsync(userId, id);

        if (!success)
        {
            return NotFound("Notification not found or access denied");
        }

        return Ok(new { success = true, message = "Notification marked as read" });
    }

    /// <summary>
    /// Archive a single notification (moves it to the Archived tab)
    /// </summary>
    [HttpPost("{id}/archive")]
    public async Task<IActionResult> Archive(int id)
    {
        var userId = GetCurrentUserId();
        var success = await _notificationService.ArchiveAsync(userId, id);

        if (!success)
        {
            return NotFound("Notification not found or access denied");
        }

        return Ok(new { success = true, message = "Notification archived" });
    }

    /// <summary>
    /// Mark all notifications as read
    /// </summary>
    [HttpPost("mark-all-read")]
    public async Task<IActionResult> MarkAllAsRead()
    {
        var userId = GetCurrentUserId();
        await _notificationService.MarkAllAsReadAsync(userId);
        return Ok(new { success = true, message = "All notifications marked as read" });
    }

    /// <summary>
    /// Create test notifications for the current user (development only)
    /// </summary>
    [HttpPost("create-test")]
    public async Task<IActionResult> CreateTestNotifications()
    {
        var userId = GetCurrentUserId();
        _logger.LogInformation("Creating test notifications for user ID: {UserId}", userId);

        await _notificationService.CreateNotificationAsync(userId, new CreateNotificationRequest
        {
            Type = "plaid_update_required",
            Title = "Bank Connection Update Required",
            Message = "Your Chase account connection needs to be updated to continue syncing transactions.",
            ActionUrl = "/update-connection/1",
            ActionText = "Update Connection",
            RelatedConnectionId = 1
        });

        await _notificationService.CreateNotificationAsync(userId, new CreateNotificationRequest
        {
            Type = "plaid_expiration_warning",
            Title = "Connection Expiring Soon",
            Message = "Your Wells Fargo connection will expire in 3 days. Please update it to avoid interruption.",
            ActionUrl = "/update-connection/2",
            ActionText = "Update Connection",
            RelatedConnectionId = 2
        });

        await _notificationService.CreateNotificationAsync(userId, new CreateNotificationRequest
        {
            Type = "account_alert",
            Title = "Large Transaction Detected",
            Message = "A transaction of $1,250.00 was detected in your checking account."
        });

        return Ok(new { success = true, message = "Test notifications created", userId });
    }

}
