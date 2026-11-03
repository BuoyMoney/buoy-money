using BuoyAI.API.Filters;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services.Bridge;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Pairs this self-hosted server with a BuoyAI Cloud account (docs/self_host/bridge.md). Three ways in: "Link with
/// BuoyAI Cloud" by code (link/device/start: the server shows a short code, the owner approves it on the Cloud page from
/// any device, and the server notices by itself), the older browser round trip kept as a fallback (link/start sends the
/// admin's browser to buoy-ai.com and it comes back to link/callback with a code), or a pairing code made in the Cloud
/// portal and entered here.
/// From then on the Cloud bank feed and the push relay recognise this server. Everything here is admin only except
/// the callback, whose one-time state is the proof, and the secrets it creates never come back out in a response.
/// </summary>
[Authorize(Roles = "Admin")]
[SelfHostOnly]
public class CloudBridgeController : BaseController
{
  public const string LinkCallbackPath = "/api/CloudBridge/link/callback";
  private const int MaxNameLength = 60;

  private readonly ICloudPairingService _pairing;
  private readonly CloudLinkStateStore _links;
  private readonly CloudDeviceLinkService _deviceLinks;
  private readonly CloudBridgeOptions _options;
  private readonly IConfiguration _configuration;
  private readonly ILogger<CloudBridgeController> _logger;

  public CloudBridgeController(ICloudPairingService pairing, CloudLinkStateStore links, CloudDeviceLinkService deviceLinks,
    IOptions<CloudBridgeOptions> options, IConfiguration configuration, ILogger<CloudBridgeController> logger)
  {
    _pairing = pairing;
    _links = links;
    _deviceLinks = deviceLinks;
    _options = options.Value;
    _configuration = configuration;
    _logger = logger;
  }

  public record PairBody(string? Code, string? Name);

  public record LinkStartBody(string? Name);

  public record LinkStart(string Url);

  /// <summary>Whether the Cloud feed is set up on this server and whether it is paired.</summary>
  [HttpGet("status")]
  public async Task<IActionResult> GetStatus(CancellationToken cancellationToken) =>
    Ok(await _pairing.GetStatusAsync(cancellationToken));

  [HttpPost("pair")]
  public async Task<IActionResult> Pair([FromBody] PairBody body, CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(body?.Code)) return BadRequest(new { message = "Enter the pairing code from your Cloud account." });
    // Under the pairing guard, which also stops a waiting code link so it cannot pair again later and replace this credential.
    var status = await _deviceLinks.RunExclusiveAsync(() => _pairing.PairAsync(body.Code, body.Name, cancellationToken), cancellationToken);
    return Ok(status);
  }

  /// <summary>
  /// "Link with BuoyAI Cloud" by code: asks the Cloud for a short code to show, and starts polling for the owner's
  /// approval in the background. Any server address works (plain http too), because nothing comes back to it.
  /// </summary>
  [HttpPost("link/device/start")]
  public async Task<IActionResult> StartDeviceLink([FromBody] LinkStartBody? body, CancellationToken cancellationToken)
  {
    var status = await _pairing.GetStatusAsync(cancellationToken);
    if (!status.Configured) return BadRequest(new { message = "The Cloud bank feed is not set up on this server (CloudBridge:Url)." });
    if (status.Paired) return Conflict(new { message = "This server is already linked. Unlink it first." });

    var name = string.IsNullOrWhiteSpace(body?.Name) ? null : body.Name.Trim();
    if (name?.Length > MaxNameLength) return BadRequest(new { message = $"Give the server a name of {MaxNameLength} characters or fewer." });

    return Ok(await _deviceLinks.StartAsync(GetCurrentUserId(), name, cancellationToken));
  }

  /// <summary>Where the code link stands (none, waiting, linked, expired, denied, failed). The app asks every few seconds.</summary>
  [HttpGet("link/device/status")]
  public IActionResult GetDeviceLinkStatus() => Ok(_deviceLinks.GetStatus());

  /// <summary>Stops waiting for approval. The code on the Cloud simply runs out.</summary>
  [HttpDelete("link/device")]
  public IActionResult CancelDeviceLink()
  {
    _deviceLinks.Cancel();
    return NoContent();
  }

  /// <summary>
  /// "Link with BuoyAI Cloud" (browser round trip, the fallback), step 1: a one-time state bound to this admin and the server name, and the buoy-ai.com
  /// address to open with it. The owner approves there and the browser comes back to <see cref="LinkCallback"/>.
  /// </summary>
  [HttpPost("link/start")]
  public async Task<IActionResult> StartLink([FromBody] LinkStartBody? body, CancellationToken cancellationToken)
  {
    var status = await _pairing.GetStatusAsync(cancellationToken);
    if (!status.Configured) return BadRequest(new { message = "The Cloud bank feed is not set up on this server (CloudBridge:Url)." });
    if (status.Paired) return Conflict(new { message = "This server is already linked. Unlink it first." });

    var name = string.IsNullOrWhiteSpace(body?.Name) ? null : body.Name.Trim();
    if (name?.Length > MaxNameLength) return BadRequest(new { message = $"Give the server a name of {MaxNameLength} characters or fewer." });

    var linkPage = ParseLinkPage(_options.LinkUrl);
    if (linkPage == null)
    {
      _logger.LogError("CloudBridge:LinkUrl is not an https address; the Cloud link cannot start");
      return BadRequest(new { message = "The Cloud link address on this server is not valid (CloudBridge:LinkUrl)." });
    }

    // buoy-ai.com only sends a code back to https (or this same machine over plain http). Say so here rather than
    // let the owner find a "not valid" page there.
    var callback = PublicOrigin() + LinkCallbackPath;
    if (CloudServersController.ParseLinkCallback(callback) == null)
    {
      return BadRequest(new
      {
        code = "link_needs_https",
        message = "Linking needs this server on an https address (BUOY_ORIGIN). Use a pairing code instead.",
      });
    }

    var shownName = name ?? Environment.MachineName;
    if (shownName.Length > MaxNameLength) shownName = shownName[..MaxNameLength];
    var state = _links.Create(GetCurrentUserId(), name);
    var url = QueryHelpers.AddQueryString(linkPage, new Dictionary<string, string?>
    {
      ["state"] = state,
      ["name"] = shownName,
      ["callback"] = callback,
    });
    return Ok(new LinkStart(url));
  }

  /// <summary>
  /// "Link with BuoyAI Cloud", step 2: where buoy-ai.com sends the browser back. Anonymous, because a full-page
  /// navigation carries no app session: the one-time state is the authority. Spends the state, pairs with the code,
  /// then sends the browser to the Cloud settings screen with <c>linked=1</c> or a short <c>link_error</c>.
  /// </summary>
  [AllowAnonymous]
  [HttpGet("link/callback")]
  public async Task<IActionResult> LinkCallback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error,
    CancellationToken cancellationToken)
  {
    Response.Headers.CacheControl = "no-store";
    Response.Headers["Referrer-Policy"] = "no-referrer";

    var link = _links.Consume(state);
    if (!string.IsNullOrEmpty(error)) return BackToSettings(error == "access_denied" ? "cancelled" : "failed");
    if (link == null) return BackToSettings("expired");
    if (string.IsNullOrWhiteSpace(code) || code.Length > 64) return BackToSettings("failed");

    try
    {
      await _deviceLinks.RunExclusiveAsync(() => _pairing.PairAsync(code, link.Name, cancellationToken), cancellationToken);
    }
    catch (DomainValidationException ex)
    {
      _logger.LogWarning("Cloud link for admin {UserId} did not pair: {Reason}", link.UserId, ex.Message);
      return BackToSettings("failed");
    }

    _logger.LogInformation("Cloud link completed for admin {UserId}", link.UserId);
    return BackToSettings(null);
  }

  /// <summary>Forgets the pairing on this server. Revoke the server in the Cloud portal too, to stop the Cloud side.</summary>
  [HttpDelete("pair")]
  public async Task<IActionResult> Unpair(CancellationToken cancellationToken)
  {
    // Under the pairing guard, so a code link mid-pairing cannot store its credential again after this; it also clears a
    // finished code link, which would otherwise still report "linked".
    await _deviceLinks.RunExclusiveAsync(async () =>
    {
      await _pairing.UnpairAsync(cancellationToken);
      return true;
    }, cancellationToken);
    return NoContent();
  }

  private IActionResult BackToSettings(string? linkError)
  {
    var target = PublicOrigin() + "/app/settings/cloud";
    return Redirect(linkError == null ? target + "?linked=1" : target + "?link_error=" + linkError);
  }

  /// <summary>This server's public address: App:BaseUrl (BUOY_ORIGIN), else the address this request came in on.</summary>
  private string PublicOrigin()
  {
    var configured = _configuration["App:BaseUrl"]?.Trim().TrimEnd('/');
    return string.IsNullOrEmpty(configured) ? $"{Request.Scheme}://{Request.Host}" : configured;
  }

  /// <summary>The link page, which must be https (plain http only for a loopback address while developing).</summary>
  public static string? ParseLinkPage(string? url)
  {
    if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
    var ok = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    return ok && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) ? uri.ToString() : null;
  }
}
