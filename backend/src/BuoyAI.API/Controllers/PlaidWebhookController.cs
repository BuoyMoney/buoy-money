using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.API.Attributes;

namespace BuoyAI.API.Controllers;

[ApiController]
[Route("api/plaid/webhook")]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class PlaidWebhookController : BaseController
{
    private readonly IPlaidWebhookService _webhookService;
    private readonly ILogger<PlaidWebhookController> _logger;

    public PlaidWebhookController(
        IPlaidWebhookService webhookService, 
        ILogger<PlaidWebhookController> logger)
    {
        _webhookService = webhookService;
        _logger = logger;
    }

    /// <summary>
    /// Handles incoming Plaid webhooks with signature validation
    /// </summary>
    [HttpPost("receive")]
    [AllowAnonymous] // Plaid webhooks don't use our auth but are validated via signature
    [ValidatePlaidWebhookSignature] // Custom validation for webhook authenticity
    public async Task<IActionResult> ReceiveWebhook([FromBody] PlaidWebhookRequest webhook)
    {
        try
        {
            _logger.LogInformation("Received Plaid webhook: {WebhookType} for item {ItemId}", 
                webhook.WebhookType, webhook.ItemId);

            await _webhookService.ProcessWebhookAsync(webhook);
            
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Plaid webhook: {WebhookType} for item {ItemId}", 
                webhook.WebhookType, webhook.ItemId);
            
            // Return 200 to prevent Plaid from retrying, but log the error
            return Ok(new { success = false, error = "Internal server error" });
        }
    }

    /// <summary>
    /// Creates an update token for a Plaid connection
    /// </summary>
    [HttpPost("update-token")]
    [Authorize]
    public async Task<ActionResult<CreateUpdateTokenResponse>> CreateUpdateToken(
        [FromBody] CreateUpdateTokenRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var response = await _webhookService.CreateUpdateTokenAsync(
                userId, request.ConnectionId, request.AccountSelectionEnabled, request.Platform);
            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unauthorized access to connection {ConnectionId}", request.ConnectionId);
            return StatusCode(403, new { message = "You don't have access to this connection" });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument creating update token for connection {ConnectionId}", request.ConnectionId);
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Updates a Plaid connection after user completes update flow
    /// </summary>
    [HttpPost("update-connection")]
    [Authorize]
    public async Task<IActionResult> UpdateConnection([FromBody] UpdateConnectionRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var success = await _webhookService.UpdateConnectionAsync(userId, request.ConnectionId, request);

            if (success)
            {
                return Ok(new { success = true, message = "Connection updated successfully" });
            }

            return BadRequest("Failed to update connection");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unauthorized access to connection {ConnectionId}", request.ConnectionId);
            return StatusCode(403, new { message = "You don't have access to this connection" });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument updating connection {ConnectionId}", request.ConnectionId);
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Gets the current status of a Plaid connection
    /// </summary>
    [HttpGet("connection/{connectionId}/status")]
    [Authorize]
    public async Task<ActionResult<ConnectionStatusResponse>> GetConnectionStatus(int connectionId)
    {
        try
        {
            var userId = GetCurrentUserId();
            var status = await _webhookService.GetConnectionStatusAsync(userId, connectionId);
            return Ok(status);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unauthorized access to connection {ConnectionId}", connectionId);
            return StatusCode(403, new { message = "You don't have access to this connection" });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Connection {ConnectionId} not found", connectionId);
            return NotFound(ex.Message);
        }
    }


}
