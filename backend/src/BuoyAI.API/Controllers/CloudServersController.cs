using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuoyAI.API.Controllers;

/// <summary>
/// The BuoyAI Cloud portal inside the hosted app: the signed-in user's self-hosted servers. It lists them,
/// makes pairing codes, confirms new servers and revokes old ones by calling the Cloud pairing API with an
/// owner token it signs itself. The Cloud account is always the signed-in user's Stripe customer, never a
/// request field. Making a code and confirming a server need a fresh step-up MFA (X-Operation-Token).
/// </summary>
[Route("api/cloud-servers")]
[Authorize]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class CloudServersController : BaseController
{
    public const ProtectedOperation StepUpOperation = ProtectedOperation.ManageCloudServers;

    private readonly ICloudPairingPortal _portal;
    private readonly IUserRepository _users;
    private readonly IStepUpAuthService _stepUp;
    private readonly IEntitlementService _entitlements;

    public CloudServersController(
        ICloudPairingPortal portal, IUserRepository users, IStepUpAuthService stepUp, IEntitlementService entitlements)
    {
        _portal = portal;
        _users = users;
        _stepUp = stepUp;
        _entitlements = entitlements;
    }

    /// <summary>Whether Settings shows "Your servers" to this user.</summary>
    public record Visibility(bool Show);

    /// <summary>
    /// Whether the hosted app's Settings should show "Your servers": only to someone who bought a Cloud add-on or
    /// already has a server on their Cloud account (revoked ones do not count). Everyone else never sees server
    /// linking. The Cloud portal pages (buoy-ai.com/cloud/) always show it and do not ask.
    /// </summary>
    [HttpGet("visibility")]
    public async Task<IActionResult> GetVisibility(CancellationToken ct)
    {
        if (!_portal.IsConfigured) return Ok(new Visibility(false));

        var user = await _users.GetByIdAsync(GetCurrentUserId());
        if (user == null) return Unauthorized();

        if (await _entitlements.HasCloudProductsAsync(user.Id)) return Ok(new Visibility(true));
        if (string.IsNullOrEmpty(user.StripeCustomerId)) return Ok(new Visibility(false));

        try
        {
            var servers = await _portal.ListServersAsync(user.StripeCustomerId, ct);
            return Ok(new Visibility(servers.Any(s => s.Status != "revoked")));
        }
        catch (CloudPortalException)
        {
            // The Cloud cannot be asked: keep the section hidden rather than show one that cannot load.
            return Ok(new Visibility(false));
        }
    }

    /// <summary>The letters a link code is made of (no vowels, no 0/O/1/I), as the Cloud issues them.</summary>
    public const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ23456789";

    /// <summary>
    /// <paramref name="raw"/> as the Cloud's canonical <c>XXXX-XXXX</c>: upper case, spaces and dashes dropped,
    /// exactly 8 letters from <see cref="UserCodeAlphabet"/>. Null for anything else, so a typo or a crafted path
    /// never reaches the Cloud.
    /// </summary>
    public static string? NormalizeUserCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 32) return null;
        var chars = raw.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray();
        if (chars.Length != 8 || chars.Any(c => !UserCodeAlphabet.Contains(c))) return null;
        return new string(chars, 0, 4) + "-" + new string(chars, 4, 4);
    }

    /// <summary>
    /// The server waiting behind a link code (the code it shows, typed or opened from its QR): name, version, when it
    /// asked and whether it is still waiting. The owner reads this before approving.
    /// </summary>
    [HttpGet("link-sessions/{userCode}")]
    public Task<IActionResult> GetLinkSession(string userCode, CancellationToken ct) =>
        WithUserCode(userCode, code =>
            Run(requireStepUp: false, async ownerId => Ok(await _portal.GetLinkSessionAsync(ownerId, code, ct))));

    /// <summary>
    /// Links the server behind the code to this account and returns the session (status <c>approved</c>). Needs step-up
    /// MFA. A code someone else (or a deny) already decided is 409 <c>already_decided</c>.
    /// </summary>
    [HttpPost("link-sessions/{userCode}/approve")]
    public Task<IActionResult> ApproveLinkSession(string userCode, CancellationToken ct) =>
        WithUserCode(userCode, code =>
            Run(requireStepUp: true, async ownerId => Ok(await _portal.ApproveLinkSessionAsync(ownerId, code, mfa: true, ct))));

    /// <summary>Refuses the server behind the code and returns the session. No step-up: saying no is always safe.</summary>
    [HttpPost("link-sessions/{userCode}/deny")]
    public Task<IActionResult> DenyLinkSession(string userCode, CancellationToken ct) =>
        WithUserCode(userCode, code =>
            Run(requireStepUp: false, async ownerId => Ok(await _portal.DenyLinkSessionAsync(ownerId, code, ct))));

    private Task<IActionResult> WithUserCode(string? raw, Func<string, Task<IActionResult>> action)
    {
        var code = NormalizeUserCode(raw);
        return code == null
            ? Task.FromResult<IActionResult>(BadRequest(new { code = "invalid_code", message = "Check the code on your server and try again." }))
            : action(code);
    }

    /// <summary>The servers on this user's Cloud account, newest first.</summary>
    [HttpGet]
    public Task<IActionResult> List(CancellationToken ct) =>
        Run(requireStepUp: false, async ownerId => Ok(await _portal.ListServersAsync(ownerId, ct)));

    /// <summary>A one-time pairing code to type into the user's own server. Needs step-up MFA.</summary>
    [HttpPost("codes")]
    public Task<IActionResult> CreateCode(CancellationToken ct) =>
        Run(requireStepUp: true, async ownerId => Ok(await _portal.CreateCodeAsync(ownerId, mfa: true, ct: ct)));

    /// <summary>The only path a server may send the browser back to (CloudBridgeController's link callback).</summary>
    public const string LinkCallbackPath = "/api/CloudBridge/link/callback";

    public record LinkBody(string? Callback, string? State, string? Name);

    /// <summary>The browser's way back to the user's server after they approve it.</summary>
    public record LinkRedirect(string Redirect);

    /// <summary>
    /// One-click link: the signed-in owner approves a self-hosted server that sent them here
    /// (<c>/app/cloud/link?state=..&amp;name=..&amp;callback=..</c>). Makes a pairing code that confirms the server on use
    /// and answers with the server's callback address carrying that code and the server's state. Needs step-up MFA,
    /// like making a code by hand. The callback must be the link callback on some server and nothing else, so the
    /// code can only travel to the address the owner was shown.
    /// </summary>
    [HttpPost("link")]
    public Task<IActionResult> Link([FromBody] LinkBody? body, CancellationToken ct)
    {
        var callback = ParseLinkCallback(body?.Callback);
        if (callback == null)
        {
            return Task.FromResult<IActionResult>(BadRequest(new { code = "invalid_callback", message = "This link is not valid. Start again from your server." }));
        }

        var state = body!.State;
        if (string.IsNullOrEmpty(state) || state.Length > 200)
        {
            return Task.FromResult<IActionResult>(BadRequest(new { code = "invalid_state", message = "This link is not valid. Start again from your server." }));
        }

        if ((body.Name?.Trim().Length ?? 0) > 60)
        {
            return Task.FromResult<IActionResult>(BadRequest(new { code = "invalid_name", message = "The server name is too long." }));
        }

        return Run(requireStepUp: true, async ownerId =>
        {
            var code = await _portal.CreateCodeAsync(ownerId, mfa: true, autoConfirm: true, ct);
            return Ok(new LinkRedirect(BuildLinkRedirect(callback, code.Code, state)));
        });
    }

    /// <summary>
    /// The server's callback, or null unless it is exactly <c>https://host[:port]/api/CloudBridge/link/callback</c>
    /// (plain http only for a loopback host) with no user info, query or fragment.
    /// </summary>
    public static Uri? ParseLinkCallback(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 2048) return null;
        // Characters a browser or Uri would quietly rewrite: refuse instead of guessing what was meant.
        if (raw.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '?' or '#' or '@')) return null;
        // No dot segments or percent escapes either: the path must already be the plain callback path.
        if (raw.Contains("..", StringComparison.Ordinal) || raw.Contains('%') || raw.Contains("/./", StringComparison.Ordinal)) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        var secure = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!secure || string.IsNullOrEmpty(uri.Host)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        if (!string.Equals(uri.AbsolutePath, LinkCallbackPath, StringComparison.Ordinal)) return null;
        return uri;
    }

    public static string BuildLinkRedirect(Uri callback, string code, string state) =>
        callback.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
        + "?code=" + Uri.EscapeDataString(code)
        + "&state=" + Uri.EscapeDataString(state);

    /// <summary>Lets a newly paired server receive data. Needs step-up MFA.</summary>
    [HttpPost("{id:guid}/confirm")]
    public Task<IActionResult> Confirm(Guid id, CancellationToken ct) =>
        Run(requireStepUp: true, async ownerId =>
        {
            await _portal.ConfirmAsync(ownerId, id, mfa: true, ct);
            return NoContent();
        });

    /// <summary>Cuts a server off from the Cloud. It must be paired again with a new code to come back.</summary>
    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Revoke(Guid id, CancellationToken ct) =>
        Run(requireStepUp: false, async ownerId =>
        {
            await _portal.RevokeAsync(ownerId, id, ct);
            return NoContent();
        });

    private async Task<IActionResult> Run(bool requireStepUp, Func<string, Task<IActionResult>> action)
    {
        if (!_portal.IsConfigured)
        {
            return NotFound(new { code = "not_available", message = "Linking servers is not available here." });
        }

        var userId = GetCurrentUserId();
        var user = await _users.GetByIdAsync(userId);
        if (user == null) return Unauthorized();

        if (string.IsNullOrEmpty(user.StripeCustomerId))
        {
            return Conflict(new { code = "no_cloud_account", message = "Buy a Cloud product first." });
        }

        if (requireStepUp)
        {
            var problem = await StepUpProblemAsync(user);
            if (problem != null) return problem;
        }

        try
        {
            return await action(user.StripeCustomerId);
        }
        catch (CloudPortalException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
        }
    }

    /// <summary>
    /// Null when this request passed step-up MFA for <see cref="StepUpOperation"/>. Only then does the owner token
    /// carry mfa=true. The step-up itself is always required. Like every other protected operation
    /// (<see cref="BuoyAI.API.Attributes.RequiresMfaAttribute"/>), it needs a verified email first, because the
    /// emailed step-up code is the fallback factor every account has: an account with no authenticator or passkey
    /// (a new Cloud-only account, say) passes step-up with that code. An account whose email is not verified is told
    /// to verify it first, which the portal can do in place.
    /// </summary>
    private async Task<IActionResult?> StepUpProblemAsync(User user)
    {
        if (!user.IsEmailVerified)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "EmailVerificationRequired",
                code = "email_verification_required",
                message = "Confirm your email address first. Then you can link a server.",
            });
        }

        var token = Request.Headers["X-Operation-Token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(token) && await _stepUp.ValidateOperationTokenAsync(token, StepUpOperation, user.Id))
        {
            return null;
        }

        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = "MfaRequired",
            message = "This operation requires multi-factor authentication",
            operation = StepUpOperation.ToString(),
            requirementLevel = _stepUp.GetRequirementLevel(StepUpOperation).ToString(),
        });
    }
}
