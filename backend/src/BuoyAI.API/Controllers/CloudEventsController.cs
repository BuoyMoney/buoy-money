using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs.Cloud;
using BuoyAI.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Receives signed events from the BuoyAI Cloud services (AI gateway allowance alerts, bridge pairing and bank
/// notices) and tells the customer. Off unless <c>CloudEvents:WebhookSecret</c> is set: until then it answers 404.
/// </summary>
// NOTE: intentionally inherits ControllerBase, not BaseController, like the Stripe webhook. This endpoint is
// unauthenticated (the Cloud service signs the body instead of sending a user token), so BaseController's user helpers
// do not apply, and the shared "api" rate limit would drop a burst of alerts.
[ApiController]
[Route("api/cloud-events")]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404. The Cloud services post to the hosted app only.
public class CloudEventsController : ControllerBase
{
    public const string SignatureHeader = "X-Buoy-Signature";
    private const int MaxBodyBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ICloudEventService _events;
    private readonly IOptions<CloudEventsOptions> _options;
    private readonly ILogger<CloudEventsController> _logger;

    public CloudEventsController(ICloudEventService events, IOptions<CloudEventsOptions> options, ILogger<CloudEventsController> logger)
    {
        _events = events;
        _options = options;
        _logger = logger;
    }

    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.IsEnabled)
        {
            return NotFound();
        }

        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await Request.Body.CopyToAsync(buffer, cancellationToken);
            body = buffer.ToArray();
        }
        // No manual length check: [RequestSizeLimit] makes the server reject an oversized body (413) while it is read.

        if (!SignatureMatches(options.WebhookSecret, body, Request.Headers[SignatureHeader].ToString()))
        {
            _logger.LogWarning("Refused a Cloud event with a missing or wrong signature");
            return Unauthorized();
        }

        CloudEvent? cloudEvent;
        try
        {
            cloudEvent = JsonSerializer.Deserialize<CloudEvent>(body, Json);
        }
        catch (JsonException)
        {
            return BadRequest(new { message = "The event is not valid JSON." });
        }
        if (cloudEvent == null)
        {
            return BadRequest(new { message = "Empty event." });
        }

        // Every outcome is a 200: an event for an account we do not know will never become deliverable by retrying.
        var outcome = await _events.HandleAsync(cloudEvent, cancellationToken);
        return Ok(new { outcome = outcome.ToString() });
    }

    /// <summary>True when <paramref name="header"/> is <c>sha256=</c> plus the lowercase hex HMAC-SHA256 of the body.</summary>
    public static bool SignatureMatches(string secret, byte[] body, string? header)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(header) || !header.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }
        var expected = Encoding.ASCII.GetBytes(
            "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant());
        var sent = Encoding.ASCII.GetBytes(header.Trim());
        // Constant time, so the signature cannot be guessed a byte at a time.
        return sent.Length == expected.Length && CryptographicOperations.FixedTimeEquals(sent, expected);
    }
}
