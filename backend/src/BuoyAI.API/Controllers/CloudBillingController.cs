using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuoyAI.API.Controllers;

/// <summary>
/// The customer's own view of their Buoy Money Cloud plan: what is on it, what each change would
/// cost, and the changes themselves (buy, add, change a quantity, remove at period end, cancel,
/// undo). The product rules and the Stripe work happen in the Cloud billing service; this controller
/// decides WHO is asking and forwards. The customer is always the signed-in user, never a request field.
/// </summary>
[Route("api/cloud-billing")]
[Authorize]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class CloudBillingController : BaseController
{
    private static readonly HashSet<string> ChangeTypes =
        ["add", "set_quantity", "remove", "undo_remove", "cancel", "undo_cancel"];

    private readonly ICloudBillingService _billing;

    public CloudBillingController(ICloudBillingService billing)
    {
        _billing = billing;
    }

    /// <summary>What the user holds now, whether they qualify for founding prices, and their plan.</summary>
    [HttpGet("status")]
    public Task<IActionResult> GetStatus(CancellationToken ct) =>
        Run(() => _billing.GetStatusAsync(GetCurrentUserId(), ct));

    /// <summary>Products and prices for this user (founding or list).</summary>
    [HttpGet("catalog")]
    public Task<IActionResult> GetCatalog(CancellationToken ct) =>
        Run(() => _billing.GetCatalogAsync(GetCurrentUserId(), ct));

    /// <summary>Starts a Stripe Checkout for a first purchase. Returns the hosted page's URL.</summary>
    [HttpPost("checkout")]
    public Task<IActionResult> StartCheckout([FromBody] CloudCheckoutBody body, CancellationToken ct)
    {
        if (body.Products is not { Count: > 0 and <= 10 })
        {
            return Task.FromResult<IActionResult>(BadRequest(new { code = "invalid_selection", message = "Choose at least one product." }));
        }
        if (body.Interval is not ("monthly" or "yearly"))
        {
            return Task.FromResult<IActionResult>(BadRequest(new { code = "invalid_selection", message = "Interval must be 'monthly' or 'yearly'." }));
        }

        return Run(async () => new { url = await _billing.StartCheckoutAsync(
            GetCurrentUserId(), body.Interval, body.Products, body.Platform, IdempotencyKey(), ct) });
    }

    /// <summary>What a change would add to the next bill, and what the plan looks like after it.</summary>
    [HttpPost("subscription/preview")]
    public Task<IActionResult> Preview([FromBody] CloudChangeBody body, CancellationToken ct)
    {
        if (!ChangeTypes.Contains(body.Type ?? ""))
        {
            return Task.FromResult<IActionResult>(BadRequest(new { code = "invalid_change", message = "Unknown change." }));
        }
        return Run(() => _billing.PreviewAsync(GetCurrentUserId(), body.Type!, body.Product, body.Quantity, body.ProrationDate, ct));
    }

    /// <summary>Makes a change. Send the preview's <c>prorationDate</c> back so the bill matches the screen.</summary>
    [HttpPost("subscription/changes")]
    public Task<IActionResult> Apply([FromBody] CloudChangeBody body, CancellationToken ct)
    {
        if (!ChangeTypes.Contains(body.Type ?? ""))
        {
            return Task.FromResult<IActionResult>(BadRequest(new { code = "invalid_change", message = "Unknown change." }));
        }
        return Run(() => _billing.ApplyAsync(
            GetCurrentUserId(), body.Type!, body.Product, body.Quantity, body.ProrationDate, IdempotencyKey(), ct));
    }

    /// <summary>The plan and the next invoice, line by line, for the billing screen.</summary>
    [HttpGet("summary")]
    public Task<IActionResult> GetSummary(CancellationToken ct) =>
        Run(() => _billing.GetSummaryAsync(GetCurrentUserId(), ct));

    /// <summary>
    /// A Stripe customer portal address for the signed-in user's card, billing details and invoices.
    /// <c>?from=portal</c> (the Cloud portal at buoy-ai.com/cloud/) makes Stripe send them back there.
    /// </summary>
    [HttpPost("portal-session")]
    public Task<IActionResult> OpenPortal([FromQuery] string? from, CancellationToken ct) =>
        Run(async () => new
        {
            url = string.Equals(from, "portal", StringComparison.OrdinalIgnoreCase)
                ? await _billing.CreatePortalSessionAsync(GetCurrentUserId(), fromPortal: true, ct)
                : await _billing.CreatePortalSessionAsync(GetCurrentUserId(), ct)
        });

    private string? IdempotencyKey()
    {
        var key = Request.Headers["Idempotency-Key"].ToString();
        // Bounded: it is forwarded to Stripe, which caps keys at 255 characters.
        return string.IsNullOrWhiteSpace(key) || key.Length > 200 ? null : key;
    }

    private async Task<IActionResult> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (CloudBillingException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
        }
    }
}

public sealed class CloudCheckoutBody
{
    public string Interval { get; set; } = "";
    public List<CloudProductSelection>? Products { get; set; }

    /// <summary>"ios" is refused: new Cloud products are not sold through Stripe inside the iOS app.</summary>
    public string? Platform { get; set; }
}

public sealed class CloudChangeBody
{
    public string? Type { get; set; }
    public string? Product { get; set; }
    public int? Quantity { get; set; }
    public DateTime? ProrationDate { get; set; }
}
