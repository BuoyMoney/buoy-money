using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.API.Attributes;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

[Authorize]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class PlaidController : BaseController
{
  private readonly IPlaidService _plaidService;
  private readonly IBankAccountService _bankAccounts;
  private readonly ITwoFactorService _twoFactorService;
  private readonly IFamilyAuthorizationService _authService;
  private readonly IEntitlementService _entitlements;
  private readonly ILogger<PlaidController> _logger;

  public PlaidController(
    IPlaidService plaidService,
    IBankAccountService bankAccounts,
    ITwoFactorService twoFactorService,
    IFamilyAuthorizationService authService,
    IEntitlementService entitlements,
    ILogger<PlaidController> logger)
  {
    _plaidService = plaidService;
    _bankAccounts = bankAccounts;
    _twoFactorService = twoFactorService;
    _authService = authService;
    _entitlements = entitlements;
    _logger = logger;
  }

  [HttpPost("create-link-token")]
  public async Task<IActionResult> CreateLinkToken([FromBody] CreateLinkTokenBody? body = null)
  {
    try
    {
      // Add debugging for JWT claims
      _logger.LogInformation("CreateLinkToken - User claims: {Claims}", 
        string.Join(", ", User.Claims.Select(c => $"{c.Type}={c.Value}")));
      
      var userId = GetCurrentUserId();
      
      // Check RBAC - only Owner and BudgetAdmin can connect bank accounts
      var authResult = await _authService.CanManageBankConnectionsAsync(userId);
      if (!authResult.IsAuthorized)
      {
        _logger.LogWarning("User {UserId} with role {Role} denied bank connection: {Reason}", 
          userId, authResult.UserRole, authResult.Reason);
        return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to connect bank accounts. Only family Owners and BudgetAdmins can manage bank connections." });
      }
      
      // Check if user has proper MFA set up before allowing Plaid connection
      var canAccessPlaid = await _twoFactorService.CanAccessPlaidFeaturesAsync(userId);
      if (!canAccessPlaid)
      {
        _logger.LogWarning("User {UserId} attempted to create link token without proper MFA", userId);
        return BadRequest(new { 
          message = "Two-factor authentication is required to connect bank accounts. Please set up MFA in your profile settings first.",
          requiresMfa = true 
        });
      }
      
      // Check subscription tier
      var canAccessFeature = await _entitlements.HasFeatureAsync(userId, "plaid_connections");
      if (!canAccessFeature)
      {
        return StatusCode(403, new { message = "Bank account connections require an active subscription or trial. Please upgrade your plan." });
      }

      var request = new CreateLinkTokenRequest { UserId = userId, Platform = body?.Platform };
      var response = await _plaidService.CreateLinkTokenAsync(request);
      
      return Ok(response);
    }
    catch (UnauthorizedAccessException ex)
    {
      _logger.LogWarning(ex, "Unauthorized access attempting to create link token");
      return Unauthorized(new { message = "Invalid user authentication" });
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error creating link token");
      return BadRequest(new { message = ex.Message });
    }
  }

  [HttpPost("exchange-public-token")]
  [RequiresMfa(ProtectedOperation.ConnectBankAccount)]
  public async Task<IActionResult> ExchangePublicToken([FromBody] ExchangePublicTokenRequest request)
  {
    if (string.IsNullOrEmpty(request.PublicToken))
    {
      return BadRequest(new { message = "Public token is required" });
    }

    var userId = GetCurrentUserId();

    // Check RBAC - only Owner and BudgetAdmin can connect bank accounts
    var authResult = await _authService.CanManageBankConnectionsAsync(userId);
    if (!authResult.IsAuthorized)
    {
      _logger.LogWarning("User {UserId} with role {Role} denied bank connection exchange: {Reason}",
        userId, authResult.UserRole, authResult.Reason);
      return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to connect bank accounts. Only family Owners and BudgetAdmins can manage bank connections." });
    }

    // Check if user has proper MFA set up before allowing Plaid connection
    var canAccessPlaid = await _twoFactorService.CanAccessPlaidFeaturesAsync(userId);
    if (!canAccessPlaid)
    {
      _logger.LogWarning("User {UserId} attempted to exchange public token without proper MFA", userId);
      return BadRequest(new {
        message = "Two-factor authentication is required to connect bank accounts. Please set up MFA in your profile settings first.",
        requiresMfa = true
      });
    }

    request.UserId = userId;
    var response = await _plaidService.ExchangePublicTokenAsync(request);

    return Ok(response);
  }

  [HttpGet("accounts")]
  public async Task<IActionResult> GetAccounts()
  {
    var userId = GetCurrentUserId();
    var accounts = await _bankAccounts.GetAccountsAsync(userId);

    return Ok(accounts);
  }

  [HttpGet("transactions")]
  public async Task<IActionResult> GetTransactions([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
  {
    var userId = GetCurrentUserId();
    var transactions = await _plaidService.GetTransactionsAsync(userId, startDate, endDate);

    return Ok(transactions);
  }

  /// <summary>
  /// Syncs transactions from Plaid for all linked accounts.
  /// This is a lightweight refresh operation that doesn't require MFA.
  /// For reconnecting accounts (which requires bank credentials), use the reconnect endpoint.
  /// </summary>
  [HttpPost("sync")]
  public async Task<IActionResult> SyncTransactions()
  {
    var userId = GetCurrentUserId();

    // Priority order (User Rules > Plaid > AI) is enforced inside the sync itself, on every
    // ingest path, by ITransactionIngestCategorizer. This endpoint only reports what happened.
    var syncResult = await _plaidService.SyncTransactionsAsync(userId);

    return Ok(new {
      message = "Sync completed successfully",
      newTransactions = syncResult.TotalNewTransactions,
      rulesApplied = syncResult.RulesApplied,
      queuedForAI = syncResult.QueuedTransactionIds.Count
    });
  }

  /// <summary>
  /// One-time repair: push the configured Plaid:WebhookUrl onto every active item via
  /// /item/webhook/update. Items created while the webhook URL was unset (pre-2026-07-18)
  /// receive no webhooks at all — no update-mode prompts, no transaction push. Admin-only.
  /// </summary>
  [HttpPost("admin/backfill-webhooks")]
  [Authorize(Roles = "Admin")]
  public async Task<IActionResult> BackfillWebhooks()
  {
    var (updated, failed) = await _plaidService.BackfillWebhookUrlsAsync();
    return Ok(new { updated, failed });
  }

  // New endpoints for accounts management
  [HttpGet("connections")]
  public async Task<IActionResult> GetConnections()
  {
    try
    {
      // Add debugging for JWT claims
      _logger.LogInformation("User claims: {Claims}", 
        string.Join(", ", User.Claims.Select(c => $"{c.Type}={c.Value}")));
      
      var userId = GetCurrentUserId();
      var connections = await _bankAccounts.GetAccountConnectionsAsync(userId);
      
      return Ok(connections);
    }
    catch (UnauthorizedAccessException ex)
    {
      _logger.LogWarning(ex, "Unauthorized access attempting to get connections");
      return Unauthorized(new { message = "Invalid user authentication" });
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "No connections found for user");
      return Ok(new List<object>()); // Return empty array instead of error
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting connections");
      return BadRequest(new { message = ex.Message });
    }
  }

  [HttpPost("exchange-public-token-with-historical-data")]
  [RequiresMfa(ProtectedOperation.ConnectBankAccount)]
  public async Task<IActionResult> ExchangePublicTokenWithHistoricalData([FromBody] ImportHistoricalDataRequest request)
  {
    try
    {
      if (string.IsNullOrEmpty(request.PublicToken))
      {
        return BadRequest(new { message = "Public token is required" });
      }

      var userId = GetCurrentUserId();
      
      // Check RBAC - only Owner and BudgetAdmin can connect bank accounts
      var authResult = await _authService.CanManageBankConnectionsAsync(userId);
      if (!authResult.IsAuthorized)
      {
        _logger.LogWarning("User {UserId} with role {Role} denied bank connection with historical data: {Reason}", 
          userId, authResult.UserRole, authResult.Reason);
        return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to connect bank accounts. Only family Owners and BudgetAdmins can manage bank connections." });
      }
      
      request.UserId = userId;
      var response = await _plaidService.ExchangePublicTokenWithImportAsync(request);
      
      return Ok(response);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("already have an active connection"))
    {
      _logger.LogWarning(ex, "Duplicate institution connection attempt for user {UserId}", GetCurrentUserId());
      return BadRequest(new { message = ex.Message });
    }
  }

  [HttpPost("import-historical-data")]
  public async Task<IActionResult> ImportHistoricalData([FromBody] ImportHistoricalDataRequest request)
  {
    if (string.IsNullOrEmpty(request.PublicToken))
    {
      return BadRequest(new { message = "Public token is required" });
    }

    var userId = GetCurrentUserId();

    // Check RBAC - only Owner and BudgetAdmin can import historical data
    var authResult = await _authService.CanManageBankConnectionsAsync(userId);
    if (!authResult.IsAuthorized)
    {
      _logger.LogWarning("User {UserId} with role {Role} denied historical data import: {Reason}",
        userId, authResult.UserRole, authResult.Reason);
      return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to import bank data. Only family Owners and BudgetAdmins can manage bank connections." });
    }

    // For this implementation, we'll use the existing exchange method and then import
    var exchangeResponse = await _plaidService.ExchangePublicTokenAsync(new ExchangePublicTokenRequest
    {
      PublicToken = request.PublicToken,
      UserId = userId
    });

    await _plaidService.ImportHistoricalDataAsync(userId, exchangeResponse.ItemId, request.Range, request.CustomStartDate, request.CustomEndDate);

    return Ok(new { message = "Historical data import completed successfully" });
  }

  [HttpDelete("connections/{connectionId}")]
  [RequiresMfa(ProtectedOperation.DisconnectBankAccount)]
  public async Task<IActionResult> DisconnectAccount(int connectionId)
  {
    var userId = GetCurrentUserId();

    // Check RBAC - only Owner and BudgetAdmin can disconnect bank accounts
    var authResult = await _authService.CanManageBankConnectionsAsync(userId);
    if (!authResult.IsAuthorized)
    {
      _logger.LogWarning("User {UserId} with role {Role} denied bank disconnection: {Reason}",
        userId, authResult.UserRole, authResult.Reason);
      return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to disconnect bank accounts. Only family Owners and BudgetAdmins can manage bank connections." });
    }

    await _plaidService.DisconnectAccountAsync(userId, connectionId);

    return Ok(new { message = "Account disconnected successfully" });
  }

  [HttpPost("connections/{connectionId}/reconnect")]
  public async Task<IActionResult> ReconnectAccount(int connectionId, [FromQuery] string? platform = null)
  {
    var userId = GetCurrentUserId();

    // Check RBAC - only Owner and BudgetAdmin can reconnect bank accounts
    var authResult = await _authService.CanManageBankConnectionsAsync(userId);
    if (!authResult.IsAuthorized)
    {
      _logger.LogWarning("User {UserId} with role {Role} denied bank reconnection: {Reason}",
        userId, authResult.UserRole, authResult.Reason);
      return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to reconnect bank accounts. Only family Owners and BudgetAdmins can manage bank connections." });
    }

    // Returns a Plaid update-mode link token — the client opens it in Plaid Link to
    // re-authenticate. The connection is reactivated only after the resulting public token is
    // exchanged (POST /plaid-webhook/update-connection); it is NOT marked healthy up front.
    var linkToken = await _plaidService.ReconnectAccountAsync(userId, connectionId, platform);

    return Ok(new { linkToken, message = "Complete reconnection in Plaid Link to restore this connection." });
  }

  [HttpGet("connections/{connectionId}/status")]
  public async Task<IActionResult> GetConnectionStatus(int connectionId)
  {
    var userId = GetCurrentUserId();
    var status = await _plaidService.GetConnectionStatusAsync(userId, connectionId);

    return Ok(status);
  }

  [HttpPost("exchange-public-token-quick-sync")]
  [RequiresMfa(ProtectedOperation.ConnectBankAccount)]
  public async Task<IActionResult> ExchangePublicTokenWithQuickSync([FromBody] ExchangePublicTokenRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      request.UserId = userId;
      
      var response = await _plaidService.ExchangePublicTokenWithQuickSyncAsync(request);
      
      return Ok(response);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("already have an active connection"))
    {
      _logger.LogWarning(ex, "Duplicate institution connection attempt for user {UserId}", GetCurrentUserId());
      return BadRequest(new { message = ex.Message });
    }
  }

  [HttpGet("connections/{connectionId}/import-progress")]
  public async Task<IActionResult> GetImportProgress(int connectionId)
  {
    var userId = GetCurrentUserId();
    var progress = await _bankAccounts.GetImportProgressAsync(userId, connectionId);

    return Ok(progress);
  }

  [HttpPut("accounts/{accountId}/nickname")]
  public async Task<IActionResult> UpdateAccountNickname(int accountId, [FromBody] UpdateAccountNicknameRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      _logger.LogInformation("Updating nickname for account {AccountId} by user {UserId}", accountId, userId);
      
      var response = await _bankAccounts.UpdateAccountNicknameAsync(userId, accountId, request.Nickname);
      
      return Ok(response);
    }
    catch (KeyNotFoundException ex)
    {
      _logger.LogWarning(ex, "Account {AccountId} not found for user {UserId}", accountId, GetCurrentUserId());
      return NotFound(new { message = "Account not found" });
    }
    catch (UnauthorizedAccessException ex)
    {
      _logger.LogWarning(ex, "User {UserId} not authorized to update account {AccountId}", GetCurrentUserId(), accountId);
      return Forbid();
    }
  }

  [HttpPut("accounts/{accountId}/association")]
  public async Task<IActionResult> SetAccountAssociation(int accountId, [FromBody] UpdateAccountAssociationRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      var auth = await _authService.CanManageBankConnectionsAsync(userId);
      if (!auth.IsAuthorized)
        return StatusCode(403, new { message = auth.Reason ?? "Only family admins can change account associations" });

      var response = await _bankAccounts.SetAccountAssociationAsync(userId, accountId, request.AssociatedUserId);
      return Ok(response);
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
