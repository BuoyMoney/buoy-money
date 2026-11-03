using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Subscription;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

[Authorize]
public class SubscriptionsController : BaseController
{
  private readonly ISubscriptionService _subscriptionService;
  private readonly ILogger<SubscriptionsController> _logger;

  public SubscriptionsController(
    ISubscriptionService subscriptionService,
    ILogger<SubscriptionsController> logger)
  {
    _subscriptionService = subscriptionService;
    _logger = logger;
  }

  [HttpGet]
  public async Task<IActionResult> GetSubscriptions()
  {
    var userId = GetCurrentUserId();
    var subscriptions = await _subscriptionService.GetSubscriptionsAsync(userId);
    return Ok(subscriptions);
  }

  [HttpGet("{id}")]
  public async Task<IActionResult> GetSubscription(int id)
  {
    var userId = GetCurrentUserId();
    var subscription = await _subscriptionService.GetSubscriptionByIdAsync(userId, id);

    if (subscription == null)
    {
      return NotFound(new { message = "Subscription not found" });
    }

    return Ok(subscription);
  }

  [HttpPost]
  public async Task<IActionResult> CreateSubscription([FromBody] CreateSubscriptionRequest request)
  {
    if (!ModelState.IsValid)
    {
      return BadRequest(ModelState);
    }

    var userId = GetCurrentUserId();
    var subscription = await _subscriptionService.CreateSubscriptionAsync(userId, request);

    return CreatedAtAction(
      nameof(GetSubscription),
      new { id = subscription.Id },
      subscription);
  }

  [HttpPut("{id}")]
  public async Task<IActionResult> UpdateSubscription(int id, [FromBody] UpdateSubscriptionRequest request)
  {
    try
    {
      if (!ModelState.IsValid)
      {
        return BadRequest(ModelState);
      }

      var userId = GetCurrentUserId();
      var subscription = await _subscriptionService.UpdateSubscriptionAsync(userId, id, request);

      return Ok(subscription);
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "Subscription {SubscriptionId} not found for update", id);
      return NotFound(new { message = ex.Message });
    }
  }

  [HttpDelete("{id}")]
  public async Task<IActionResult> DeleteSubscription(int id)
  {
    try
    {
      var userId = GetCurrentUserId();
      await _subscriptionService.DeleteSubscriptionAsync(userId, id);

      return NoContent();
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "Subscription {SubscriptionId} not found for deletion", id);
      return NotFound(new { message = ex.Message });
    }
  }

  [HttpGet("detect/cooldown")]
  public async Task<IActionResult> GetDetectionCooldown()
  {
    var userId = GetCurrentUserId();
    var canRun = await _subscriptionService.CanRunOnDemandDetectionAsync(userId);
    return Ok(new { canRun });
  }

  [HttpPost("detect")]
  public async Task<IActionResult> DetectSubscriptions([FromBody] SubscriptionDetectionRequest? request = null)
  {
    var userId = GetCurrentUserId();

    // Enforce the on-demand cooldown server-side. The FE also gates this, but a client that skips
    // the gate could otherwise re-run detection (and its AI enhancement) back-to-back. 429 signals
    // the cooldown is active; the FE reads GET detect/cooldown to know when it may retry.
    if (!await _subscriptionService.CanRunOnDemandDetectionAsync(userId))
    {
      _logger.LogInformation("Rejecting on-demand detection for user {UserId} - cooldown active", userId);
      return StatusCode(StatusCodes.Status429TooManyRequests, new
      {
        message = "Subscription detection was run recently. Please try again later.",
        canRun = false
      });
    }

    // isOnDemand: true — this is the user-initiated "Detect" button, so it (and only it, plus the
    // Plaid-sync trigger) stamps the per-user on-demand cooldown clock. The weekly background job
    // must not, or every user would be stuck on cooldown after each weekly sweep.
    var detectedSubscriptions = await _subscriptionService.DetectSubscriptionsAsync(userId, request, isOnDemand: true);

    return Ok(detectedSubscriptions);
  }

  [HttpPost("confirm")]
  public async Task<IActionResult> ConfirmDetectedSubscription([FromBody] DetectedSubscriptionDto detectedSubscription)
  {
    if (!ModelState.IsValid)
    {
      return BadRequest(ModelState);
    }

    var userId = GetCurrentUserId();
    var subscription = await _subscriptionService.ConfirmDetectedSubscriptionAsync(userId, detectedSubscription);

    return Ok(subscription);
  }

  [HttpPost("{id}/dismiss")]
  public async Task<IActionResult> DismissDetectedSubscription(int id, [FromBody] DismissSubscriptionRequest? request = null)
  {
    var userId = GetCurrentUserId();

    // For backward compatibility, if no request body, require merchantName from route
    // But since we have the id, we need to get the merchant name somehow
    // Better approach: use a new endpoint for dismissing by merchant name
    if (request == null || string.IsNullOrEmpty(request.MerchantName))
    {
      return BadRequest(new { message = "MerchantName is required" });
    }

    await _subscriptionService.DismissDetectedSubscriptionAsync(
      userId, request.MerchantName, request.Amount, request.Reason);

    return Ok(new { message = "Detected subscription dismissed" });
  }

  [HttpPost("dismiss")]
  public async Task<IActionResult> DismissDetectedSubscriptionByMerchant([FromBody] DismissSubscriptionRequest request)
  {
    if (string.IsNullOrEmpty(request.MerchantName))
    {
      return BadRequest(new { message = "MerchantName is required" });
    }

    var userId = GetCurrentUserId();
    await _subscriptionService.DismissDetectedSubscriptionAsync(
      userId, request.MerchantName, request.Amount, request.Reason);

    return Ok(new { message = "Detected subscription dismissed" });
  }

  [HttpGet("pending")]
  public async Task<IActionResult> GetPendingDetectedSubscriptions()
  {
    var userId = GetCurrentUserId();
    var pending = await _subscriptionService.GetPendingDetectedSubscriptionsAsync(userId);

    return Ok(pending);
  }

  [HttpGet("calendar")]
  public async Task<IActionResult> GetCalendarData(
    [FromQuery] DateTime startDate,
    [FromQuery] DateTime endDate)
  {
    var userId = GetCurrentUserId();
    var calendarData = await _subscriptionService.GetCalendarDataAsync(userId, startDate, endDate);

    return Ok(calendarData);
  }

  /// <summary>
  /// Get calendar billing events with past (confirmed), next (upcoming), and future (projected) charges.
  /// Past charges are grey, next due is green, future projections are purple.
  /// </summary>
  [HttpGet("calendar/events")]
  public async Task<IActionResult> GetCalendarBillingEvents(
    [FromQuery] DateTime startDate,
    [FromQuery] DateTime endDate)
  {
    var userId = GetCurrentUserId();
    var events = await _subscriptionService.GetCalendarBillingEventsAsync(userId, startDate, endDate);

    return Ok(events);
  }

  /// <summary>
  /// Get timeline data for subscriptions including past transactions and future projections.
  /// </summary>
  [HttpGet("timeline")]
  public async Task<IActionResult> GetTimeline([FromQuery] SubscriptionTimelineRequest? request = null)
  {
    var userId = GetCurrentUserId();
    var timeline = await _subscriptionService.GetTimelineDataAsync(userId, request);

    return Ok(timeline);
  }

  /// <summary>
  /// Get subscriptions that appear to be cancelled (no charges in 2+ billing cycles).
  /// These should be reviewed by the user to confirm cancellation.
  /// </summary>
  [HttpGet("potentially-cancelled")]
  public async Task<IActionResult> GetPotentiallyCancelled()
  {
    var userId = GetCurrentUserId();
    var cancelled = await _subscriptionService.GetPotentiallyCancelledSubscriptionsAsync(userId);

    return Ok(cancelled);
  }

  [HttpGet("summary")]
  public async Task<IActionResult> GetSummary()
  {
    var userId = GetCurrentUserId();
    var summary = await _subscriptionService.GetSubscriptionSummaryAsync(userId);

    return Ok(summary);
  }

  /// <summary>
  /// Toggle the HiddenFromReadOnly flag for a subscription (for gift hiding from family members).
  /// </summary>
  [HttpPatch("{id}/visibility")]
  public async Task<IActionResult> UpdateVisibility(int id, [FromBody] UpdateSubscriptionVisibilityRequest request)
  {
    var userId = GetCurrentUserId();
    var subscription = await _subscriptionService.GetSubscriptionByIdAsync(userId, id);

    if (subscription == null)
    {
      return NotFound(new { message = "Subscription not found" });
    }

    // Use the update method to toggle visibility
    var updateRequest = new UpdateSubscriptionRequest
    {
      HiddenFromReadOnly = request.HiddenFromReadOnly
    };

    var updated = await _subscriptionService.UpdateSubscriptionAsync(userId, id, updateRequest);

    _logger.LogInformation("User {UserId} set subscription {SubscriptionId} visibility to HiddenFromReadOnly={Hidden}",
      userId, id, request.HiddenFromReadOnly);

    return Ok(updated);
  }

  /// <summary>
  /// Batch update visibility for multiple subscriptions.
  /// </summary>
  [HttpPatch("visibility/batch")]
  public async Task<IActionResult> UpdateVisibilityBatch([FromBody] BatchUpdateVisibilityRequest request)
  {
    var userId = GetCurrentUserId();
    var results = new List<object>();

    foreach (var id in request.SubscriptionIds)
    {
      try
      {
        var subscription = await _subscriptionService.GetSubscriptionByIdAsync(userId, id);
        if (subscription == null) continue;

        var updateRequest = new UpdateSubscriptionRequest
        {
          HiddenFromReadOnly = request.HiddenFromReadOnly
        };

        await _subscriptionService.UpdateSubscriptionAsync(userId, id, updateRequest);
        results.Add(new { id, success = true });
      }
      catch
      {
        results.Add(new { id, success = false });
      }
    }

    _logger.LogInformation("User {UserId} batch updated {Count} subscription visibility to HiddenFromReadOnly={Hidden}",
      userId, request.SubscriptionIds.Count, request.HiddenFromReadOnly);

    return Ok(new { updated = results.Count(r => ((dynamic)r).success), results });
  }
}

public class UpdateSubscriptionVisibilityRequest
{
  public bool HiddenFromReadOnly { get; set; }
}

public class BatchUpdateVisibilityRequest
{
  public List<int> SubscriptionIds { get; set; } = new();
  public bool HiddenFromReadOnly { get; set; }
}
