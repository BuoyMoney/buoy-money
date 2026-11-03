using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Receives RevenueCat webhook events (App Store and Play subscription lifecycle).
/// </summary>
// NOTE: inherits ControllerBase, not BaseController, for the same reason StripeWebhookController
// does: this endpoint is unauthenticated at the user level (RevenueCat authenticates with the
// shared Authorization header), and the shared "api" rate limiter must not drop retries.
[ApiController]
[AllowAnonymous]
[Route("api/billing/revenuecat/webhook")]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class RevenueCatWebhookController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly IRevenueCatSyncService _sync;
    private readonly ILogger<RevenueCatWebhookController> _logger;

    public RevenueCatWebhookController(IRevenueCatSyncService sync, ILogger<RevenueCatWebhookController> logger)
    {
        _sync = sync;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (!_sync.IsConfigured)
            return NotFound();

        if (!_sync.IsWebhookAuthorized(Request.Headers.Authorization.ToString()))
        {
            _logger.LogWarning("RevenueCat webhook rejected: bad Authorization header");
            return Unauthorized();
        }

        RevenueCatWebhookEnvelope? envelope;
        try
        {
            envelope = await JsonSerializer.DeserializeAsync<RevenueCatWebhookEnvelope>(Request.Body, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "RevenueCat webhook body was not valid JSON");
            return BadRequest(new { message = "invalid json" });
        }

        var evt = envelope?.Event;
        if (evt == null || string.IsNullOrEmpty(evt.Type))
            return BadRequest(new { message = "missing event" });

        if (await _sync.HasProcessedEventAsync(evt.Id, ct))
        {
            _logger.LogInformation("RevenueCat event {EventId} ({Type}) already processed", evt.Id, evt.Type);
            return Ok(new { applied = false, reason = "duplicate" });
        }

        // A data problem (unknown user, unknown product, sandbox in prod) is answered 200 so
        // RevenueCat stops retrying; only a real exception (DB down) is allowed to bubble into a
        // 500, which is what earns a retry.
        var result = await _sync.ApplyEventAsync(evt, ct);
        await _sync.MarkEventProcessedAsync(evt.Id, evt.Type, ct);

        _logger.LogInformation("RevenueCat {Type} {EventId}: applied={Applied} ({Reason}) user={UserId}",
            evt.Type, evt.Id, result.Applied, result.Reason, result.UserId);
        return Ok(new { applied = result.Applied, reason = result.Reason });
    }
}
