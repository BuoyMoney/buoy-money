using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Provider-neutral bank-connection endpoints (#310). Dispatches to whichever
/// <see cref="IBankAggregator"/> is registered for the requested <see cref="BankProvider"/>, so a
/// new provider (MX, Teller, …) is reachable the moment it's registered — no new controller needed.
/// The existing Plaid-specific <c>/api/plaid/*</c> routes remain for the current client; new
/// providers use these generic routes.
/// </summary>
[Authorize]
[Route("api/aggregators")]
public class AggregatorController : BaseController
{
    private readonly IBankAggregatorRegistry _registry;
    private readonly IBankConnectionService _connections;
    private readonly IFamilyAuthorizationService _authService;
    private readonly ILogger<AggregatorController> _logger;

    public AggregatorController(
        IBankAggregatorRegistry registry,
        IBankConnectionService connections,
        IFamilyAuthorizationService authService,
        ILogger<AggregatorController> logger)
    {
        _registry = registry;
        _connections = connections;
        _authService = authService;
        _logger = logger;
    }

    /// <summary>Providers that currently have a registered implementation (e.g. ["Plaid"]).</summary>
    [HttpGet]
    public IActionResult GetAvailableProviders()
        => Ok(_registry.AvailableProviders.Select(p => p.ToString()).ToArray());

    /// <summary>Begin a connection: returns what the client launches (link token / widget URL / redirect).</summary>
    [HttpPost("{provider}/connect/start")]
    public async Task<IActionResult> StartConnection(BankProvider provider, [FromBody] StartConnectionBody? body = null)
    {
        if (!_registry.TryGet(provider, out var aggregator) || aggregator is null)
            return NotFound(new { message = $"Provider '{provider}' is not available." });

        var userId = GetCurrentUserId();
        var denied = await DenyUnlessCanManageBanksAsync(userId);
        if (denied != null) return denied;

        var start = await aggregator.StartConnectionAsync(userId, body?.Platform);
        return Ok(start);
    }

    /// <summary>Sign in again for one of the user's own connections that needs it (providers with their own sign-in page).</summary>
    [HttpPost("{provider}/connections/{connectionId:int}/reconnect")]
    public async Task<IActionResult> ReconnectConnection(BankProvider provider, int connectionId, [FromBody] StartConnectionBody? body = null)
    {
        if (!_registry.TryGet(provider, out var aggregator) || aggregator is null)
            return NotFound(new { message = $"Provider '{provider}' is not available." });

        var userId = GetCurrentUserId();
        var denied = await DenyUnlessCanManageBanksAsync(userId);
        if (denied != null) return denied;

        try
        {
            return Ok(await aggregator.StartReconnectAsync(userId, connectionId, body?.Platform));
        }
        catch (NotSupportedException)
        {
            return BadRequest(new { message = $"{provider} connections are reconnected another way." });
        }
    }

    /// <summary>Complete a connection from the provider's post-connect artifact (public_token / member_guid).</summary>
    [HttpPost("{provider}/connect/complete")]
    public async Task<IActionResult> CompleteConnection(BankProvider provider, [FromBody] CompleteConnectionBody body)
    {
        if (string.IsNullOrWhiteSpace(body?.Artifact))
            return BadRequest(new { message = "artifact is required." });

        if (!_registry.TryGet(provider, out var aggregator) || aggregator is null)
            return NotFound(new { message = $"Provider '{provider}' is not available." });

        var userId = GetCurrentUserId();
        var denied = await DenyUnlessCanManageBanksAsync(userId);
        if (denied != null) return denied;

        var result = await aggregator.CompleteConnectionAsync(userId, body.Artifact);
        return Ok(result);
    }

    /// <summary>The user's bank connections (and their family's), every provider, with status.</summary>
    [HttpGet("connections")]
    public async Task<IActionResult> GetConnections()
        => Ok(await _connections.ListAsync(GetCurrentUserId()));

    /// <summary>Disconnect one of the user's own connections, through the provider it came from.</summary>
    [HttpDelete("connections/{connectionId:int}")]
    public async Task<IActionResult> Disconnect(int connectionId)
    {
        var userId = GetCurrentUserId();
        var denied = await DenyUnlessCanManageBanksAsync(userId);
        if (denied != null) return denied;

        return await _connections.DisconnectAsync(userId, connectionId)
            ? Ok(new { message = "Connection removed." })
            : NotFound(new { message = "Connection not found." });
    }

    // Same rule the Plaid endpoints apply: only a family Owner or BudgetAdmin manages bank connections.
    private async Task<IActionResult?> DenyUnlessCanManageBanksAsync(int userId)
    {
        var auth = await _authService.CanManageBankConnectionsAsync(userId);
        if (auth.IsAuthorized) return null;

        _logger.LogWarning("User {UserId} with role {Role} denied bank connection change: {Reason}",
            userId, auth.UserRole, auth.Reason);
        return StatusCode(403, new { message = auth.Reason ?? "You don't have permission to manage bank connections." });
    }

    public class StartConnectionBody
    {
        /// <summary>Client platform hint: "web" / "android" / "ios".</summary>
        public string? Platform { get; set; }
    }

    public class CompleteConnectionBody
    {
        /// <summary>The provider's post-connect artifact — Plaid public_token, MX member_guid, etc.</summary>
        public string Artifact { get; set; } = string.Empty;
    }
}
