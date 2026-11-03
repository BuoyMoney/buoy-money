using BuoyAI.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Sign-up lock for a self-hosted server: who may create an account (see <see cref="RegistrationGate"/>).
/// Every route answers 404 on the hosted service, where sign-up is open and these rules do not exist.
/// </summary>
public class RegistrationController : BaseController
{
  private readonly RegistrationGate? _gate;

  public RegistrationController(IServiceProvider services) =>
    _gate = (RegistrationGate?)services.GetService(typeof(RegistrationGate));

  public record SetOpenRequest(bool Open);
  public record CreateInviteRequest(string? Note, int? ValidDays);

  /// <summary>What the sign-up screen needs: can a stranger register, is this the first-run screen.</summary>
  [HttpGet("status")]
  [AllowAnonymous]
  public async Task<IActionResult> GetStatus()
  {
    if (_gate == null) return NotFound();
    var status = await _gate.GetStatusAsync();
    return Ok(new { firstAccount = status.FirstAccount, open = status.Open, requiresInvite = !status.CanRegister });
  }

  [HttpGet("admin")]
  [Authorize(Roles = "Admin")]
  public async Task<IActionResult> GetAdmin()
  {
    if (_gate == null) return NotFound();
    var status = await _gate.GetStatusAsync();
    var invites = await _gate.ListInvitesAsync();
    return Ok(new
    {
      open = status.Open,
      invites = invites.Select(i => new
      {
        i.Id, i.Note, i.CreatedAt, i.ExpiresAt, i.UsedAt,
        used = i.UsedAt != null,
        expired = i.UsedAt == null && i.ExpiresAt <= DateTime.UtcNow,
      })
    });
  }

  [HttpPut("open")]
  [Authorize(Roles = "Admin")]
  public async Task<IActionResult> SetOpen([FromBody] SetOpenRequest request)
  {
    if (_gate == null) return NotFound();
    await _gate.SetOpenAsync(request.Open);
    return Ok(new { open = request.Open });
  }

  /// <summary>The code is in this response and nowhere else: only its hash is stored.</summary>
  [HttpPost("invites")]
  [Authorize(Roles = "Admin")]
  public async Task<IActionResult> CreateInvite([FromBody] CreateInviteRequest request)
  {
    if (_gate == null) return NotFound();
    var (invite, code) = await _gate.CreateInviteAsync(GetCurrentUserId(), request.Note, request.ValidDays ?? 7);
    return Ok(new { invite.Id, code, invite.ExpiresAt, invite.Note });
  }

  [HttpDelete("invites/{id:int}")]
  [Authorize(Roles = "Admin")]
  public async Task<IActionResult> RevokeInvite(int id)
  {
    if (_gate == null) return NotFound();
    return await _gate.RevokeInviteAsync(id) ? Ok(new { revoked = true }) : NotFound();
  }
}
