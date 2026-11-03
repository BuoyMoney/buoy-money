using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// What the Accounts page reads and edits, whichever bank provider a connection came from. Unlike the
/// Plaid routes (hosted only), these answer in every edition, so a self-hosted server with SimpleFIN or
/// the Cloud bank feed loads the page too. The Plaid routes stay for hosted and for older apps, and
/// return the same shapes. Connecting and disconnecting a bank lives in <see cref="AggregatorController"/>.
/// </summary>
[Authorize]
[Route("api/bank")]
public class BankAccountsController : BaseController
{
  private readonly IBankAccountService _accounts;
  private readonly IBankAggregatorRegistry _registry;
  private readonly ICloudPairingService _pairing;
  private readonly IFamilyAuthorizationService _authService;
  private readonly ILogger<BankAccountsController> _logger;

  public BankAccountsController(
    IBankAccountService accounts,
    IBankAggregatorRegistry registry,
    ICloudPairingService pairing,
    IFamilyAuthorizationService authService,
    ILogger<BankAccountsController> logger)
  {
    _accounts = accounts;
    _registry = registry;
    _pairing = pairing;
    _authService = authService;
    _logger = logger;
  }

  /// <summary>
  /// The ways this server can connect a bank right now, for the Add a bank chooser: "Plaid", "SimpleFin"
  /// and "Bridge" (the Cloud bank feed, listed only once the server is paired). Importing a file is always
  /// possible and is not listed. Every signed-in member can ask, unlike the admin-only pairing screen.
  /// </summary>
  [HttpGet("providers")]
  public async Task<IActionResult> GetProviders(CancellationToken cancellationToken)
  {
    var providers = _registry.AvailableProviders.ToList();
    if (providers.Contains(BankProvider.Bridge) && !(await _pairing.GetStatusAsync(cancellationToken)).Paired)
    {
      providers.Remove(BankProvider.Bridge);
    }
    return Ok(providers.Select(p => p.ToString()).ToArray());
  }

  [HttpGet("accounts")]
  public async Task<IActionResult> GetAccounts() =>
    Ok(await _accounts.GetAccountsAsync(GetCurrentUserId()));

  [HttpGet("connections")]
  public async Task<IActionResult> GetConnections() =>
    Ok(await _accounts.GetAccountConnectionsAsync(GetCurrentUserId()));

  [HttpGet("connections/{connectionId:int}/import-progress")]
  public async Task<IActionResult> GetImportProgress(int connectionId)
  {
    try
    {
      return Ok(await _accounts.GetImportProgressAsync(GetCurrentUserId(), connectionId));
    }
    catch (ArgumentException)
    {
      return NotFound(new { message = "Connection not found" });
    }
  }

  [HttpPut("accounts/{accountId:int}/nickname")]
  public async Task<IActionResult> UpdateAccountNickname(int accountId, [FromBody] UpdateAccountNicknameRequest request)
  {
    try
    {
      return Ok(await _accounts.UpdateAccountNicknameAsync(GetCurrentUserId(), accountId, request.Nickname));
    }
    catch (KeyNotFoundException)
    {
      return NotFound(new { message = "Account not found" });
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
  }

  [HttpPut("accounts/{accountId:int}/association")]
  public async Task<IActionResult> SetAccountAssociation(int accountId, [FromBody] UpdateAccountAssociationRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      var auth = await _authService.CanManageBankConnectionsAsync(userId);
      if (!auth.IsAuthorized)
        return StatusCode(403, new { message = auth.Reason ?? "Only family admins can change account associations" });

      return Ok(await _accounts.SetAccountAssociationAsync(userId, accountId, request.AssociatedUserId));
    }
    catch (KeyNotFoundException)
    {
      return NotFound(new { message = "Account not found" });
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }
}
