using BuoyAI.API.Filters;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Infrastructure.Services.Bridge;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuoyAI.API.Controllers;

/// <summary>
/// The Cloud plan and next invoice of the account this self-hosted server is paired with (docs/self_host/bridge.md), and a
/// Stripe customer portal address for its card and invoices. The hosted service has its own
/// <see cref="CloudBillingController"/>; a self-hosted server has no Stripe customer of its own, so this asks the Cloud
/// bridge as the paired server and the bridge answers for that pairing's account. Server administrator only, like the pairing.
/// </summary>
[Route("api/cloud/billing")]
[Authorize(Roles = "Admin")]
[SelfHostOnly]
public class CloudAccountBillingController : BaseController
{
  private readonly BridgeClient? _bridge;

  // Absent where CloudBridge:Url is not set: nothing is paired, and every route says so.
  public CloudAccountBillingController(BridgeClient? bridge = null)
  {
    _bridge = bridge;
  }

  /// <summary>The plan and the next invoice, line by line. Same shape as <c>GET /api/cloud-billing/summary</c> on the hosted service.</summary>
  [HttpGet]
  public Task<IActionResult> GetSummary(CancellationToken cancellationToken) =>
    Run(async () => Ok(await _bridge!.GetBillingAsync(cancellationToken) ?? new CloudBillingSummary(null, null)));

  /// <summary>A Stripe customer portal address for the paired account's payment method and invoices.</summary>
  [HttpPost("portal-session")]
  public Task<IActionResult> OpenPortal(CancellationToken cancellationToken) =>
    Run(async () =>
    {
      // Stripe only sends people back to https. Behind a plain-http LAN address the Cloud uses its own page instead.
      string? back = Request.IsHttps ? $"{Request.Scheme}://{Request.Host}/" : null;
      var url = await _bridge!.CreateBillingPortalAsync(back, cancellationToken);
      return string.IsNullOrEmpty(url)
        ? StatusCode(502, new { code = "empty", message = "The Cloud did not return a billing page." })
        : Ok(new { url });
    });

  private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
  {
    if (_bridge == null) return NotFound(new { message = "The Cloud is not set up on this server." });
    try
    {
      return await action();
    }
    catch (BridgeCloudException ex)
    {
      // A rejected credential is this server's problem (409), never a 401: that would sign the person out of their own server.
      var status = ex.CredentialRejected ? 409 : ex.Status is >= 400 and < 600 ? ex.Status : 502;
      return StatusCode(status, new { code = ex.Error, message = ex.Message });
    }
  }
}
