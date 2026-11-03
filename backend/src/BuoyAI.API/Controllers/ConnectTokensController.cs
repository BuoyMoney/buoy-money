using BuoyAI.API.Filters;
using BuoyAI.Core.DTOs.OAuth;
using BuoyAI.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Personal access tokens for BuoyAI Connect, at <c>/api/connect/tokens</c>: a token to paste into a tool
/// like Open WebUI so it can call this server's <c>/mcp</c> tools. Self-hosted servers only, and only
/// when Connect is turned on (<c>Mcp:Enabled</c>).
/// </summary>
[Route("api/connect/tokens")]
[Authorize]
[SelfHostOnly]
[RequireMcpEnabled]
public class ConnectTokensController : BaseController
{
  private readonly IConnectPersonalTokenService _tokens;

  public ConnectTokensController(IConnectPersonalTokenService tokens)
  {
    _tokens = tokens;
  }

  [HttpGet]
  public async Task<IActionResult> List(CancellationToken ct) =>
    Ok(await _tokens.ListAsync(GetCurrentUserId(), ct));

  [HttpPost]
  public async Task<IActionResult> Create([FromBody] CreateConnectPersonalTokenRequest request, CancellationToken ct)
  {
    try
    {
      return Ok(await _tokens.CreateAsync(GetCurrentUserId(), request.Name ?? string.Empty, ct));
    }
    catch (ArgumentException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
      return Conflict(new { message = ex.Message });
    }
  }

  [HttpDelete("{id:guid}")]
  public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) =>
    await _tokens.RevokeAsync(GetCurrentUserId(), id, ct) ? NoContent() : NotFound();
}
