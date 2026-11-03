using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Debug endpoints for development and staging environments only.
/// Provides JWT token generation for API testing without requiring login credentials.
/// In staging, requires a shared secret via X-Debug-Key header (set via Debug:ApiKey config).
/// In development, no secret is required (Debug builds only; a Release build always needs the key).
/// The routes are not registered at all unless <see cref="Extensions.DebugEndpointAvailability"/> allows them.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("api")]
public class DebugController : BaseController
{
  private readonly IAuthService _authService;
  private readonly IUserRepository _userRepository;
  private readonly IHostEnvironment _environment;
  private readonly IConfiguration _configuration;
  private readonly ILogger<DebugController> _logger;
  private readonly BuoyAIDbContext _context;

  public DebugController(
    IAuthService authService,
    IUserRepository userRepository,
    IHostEnvironment environment,
    IConfiguration configuration,
    ILogger<DebugController> logger,
    BuoyAIDbContext context)
  {
    _authService = authService;
    _userRepository = userRepository;
    _environment = environment;
    _configuration = configuration;
    _logger = logger;
    _context = context;
  }

  /// <summary>
  /// Generate a JWT token for a user by ID. Development/Staging only.
  /// In staging, requires X-Debug-Key header matching the configured Debug:ApiKey secret.
  /// Usage: GET /api/debug/token?userId=2
  ///        GET /api/debug/token?email=user@example.com
  /// </summary>
  [HttpGet("token")]
  [AllowAnonymous]
  public async Task<IActionResult> GetDebugToken([FromQuery] int? userId, [FromQuery] string? email)
  {
    if (_environment.IsProduction())
    {
      return NotFound();
    }

    if (!ValidateDebugAccess())
    {
      return Unauthorized(new { message = "Invalid or missing X-Debug-Key header" });
    }

    if (userId == null && string.IsNullOrEmpty(email))
    {
      return BadRequest(new { message = "Provide either 'userId' or 'email' query parameter" });
    }

    Core.Entities.User? user;

    if (userId.HasValue)
    {
      user = await _userRepository.GetByIdAsync(userId.Value);
    }
    else
    {
      user = await _userRepository.GetByEmailAsync(email!);
    }

    if (user == null)
    {
      return NotFound(new { message = "User not found" });
    }

    var token = _authService.GenerateJwtTokenWithClaims(user.Id, user.Username, _authService.ResolveTierClaim(user));

    _logger.LogInformation("Debug token generated for user {UserId} in {Environment}",
      user.Id, _environment.EnvironmentName);

    return Ok(new
    {
      token,
      userId = user.Id,
      email = user.Email,
      username = user.Username,
      subscriptionTier = user.SubscriptionTier?.ToString(),
      expiresIn = "24h",
      usage = $"curl -H 'Authorization: Bearer {{token}}' http://localhost:5000/api/budget/active"
    });
  }

  /// <summary>
  /// Quick health check for debug endpoints. Development/Staging only.
  /// </summary>
  [HttpGet("ping")]
  [AllowAnonymous]
  public IActionResult Ping()
  {
    if (_environment.IsProduction())
    {
      return NotFound();
    }

    if (!ValidateDebugAccess())
    {
      return Unauthorized();
    }

    return Ok(new { environment = _environment.EnvironmentName, timestamp = DateTime.UtcNow });
  }

  /// <summary>
  /// Returns raw DB counts for accounts and transactions for a user.
  /// Helps diagnose "0 transactions" issues by revealing whether data exists at all.
  /// Usage: GET /api/debug/diagnostics?userId=2
  ///        GET /api/debug/diagnostics (uses authenticated user from JWT)
  /// </summary>
  [HttpGet("diagnostics")]
  [AllowAnonymous]
  public async Task<IActionResult> GetDiagnostics([FromQuery] int? userId)
  {
    if (_environment.IsProduction())
      return NotFound();

    if (!ValidateDebugAccess())
      return Unauthorized(new { message = "Invalid or missing X-Debug-Key header" });

    int targetUserId;
    if (userId.HasValue)
    {
      targetUserId = userId.Value;
    }
    else
    {
      // Try to use authenticated user
      var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? User.FindFirst("nameid")?.Value;
      if (!int.TryParse(userIdClaim, out targetUserId))
        return BadRequest(new { message = "Provide userId query param or authenticate first" });
    }

    var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == targetUserId);
    if (user == null)
      return NotFound(new { message = $"User {targetUserId} not found" });

    var now = DateTime.UtcNow;
    var thirtyDaysAgo = now.AddDays(-30);

    // Raw account counts (ignore IsActive)
    var allAccounts = await _context.Accounts
      .Where(a => a.UserId == targetUserId)
      .Select(a => new { a.Id, a.Name, a.PlaidAccountId, a.IsActive, a.FamilyId, a.UserId })
      .ToListAsync();

    // Family accounts if user has FamilyId
    var familyAccounts = user.FamilyId.HasValue
      ? await _context.Accounts
          .Where(a => a.FamilyId == user.FamilyId)
          .Select(a => new { a.Id, a.Name, a.IsActive, a.FamilyId, a.UserId })
          .ToListAsync()
      : null;

    // Raw transaction counts (no date filter, join through Account)
    var allTxCount = await _context.Transactions
      .Where(t => t.Account.UserId == targetUserId)
      .CountAsync();

    var last30DaysTxCount = await _context.Transactions
      .Where(t => t.Account.UserId == targetUserId && t.Date >= thirtyDaysAgo && t.Date <= now)
      .CountAsync();

    // Family transaction count if applicable
    int? familyTxCount = user.FamilyId.HasValue
      ? await _context.Transactions
          .Where(t => t.Account.UserId == targetUserId || t.Account.FamilyId == user.FamilyId)
          .CountAsync()
      : null;

    // Active budgets with period dates
    var budgets = await _context.Budgets
      .Where(b => b.UserId == targetUserId && b.IsActive)
      .Select(b => new
      {
        b.Id, b.Name, b.Type, b.IsActive,
        b.CurrentPeriodStart, b.CurrentPeriodEnd,
        b.FamilyId, b.UserId
      })
      .ToListAsync();

    // Sample of 5 most recent transactions
    var recentTransactions = await _context.Transactions
      .Include(t => t.Account)
      .Where(t => t.Account.UserId == targetUserId)
      .OrderByDescending(t => t.Date)
      .Take(5)
      .Select(t => new { t.Id, t.Date, t.Amount, t.Description, t.IsPending, AccountUserId = t.Account.UserId, t.AccountId })
      .ToListAsync();

    _logger.LogInformation(
      "Debug diagnostics for user {UserId}: accounts={AccountCount}, allTxns={AllTx}, last30days={Last30Tx}",
      targetUserId, allAccounts.Count, allTxCount, last30DaysTxCount);

    return Ok(new
    {
      userId = targetUserId,
      userEmail = user.Email,
      userFamilyId = user.FamilyId,
      serverTime = now,
      accounts = new
      {
        totalByUserId = allAccounts.Count,
        activeByUserId = allAccounts.Count(a => a.IsActive),
        inactiveByUserId = allAccounts.Count(a => !a.IsActive),
        familyAccountsTotal = familyAccounts?.Count,
        list = allAccounts
      },
      transactions = new
      {
        totalByUserId = allTxCount,
        last30DaysByUserId = last30DaysTxCount,
        familyOrUserTotal = familyTxCount,
        recentSample = recentTransactions
      },
      budgets = budgets
    });
  }

  /// <summary>
  /// Validates debug access. In Development, always allowed.
  /// In Staging (or other non-prod), requires X-Debug-Key header matching Debug:ApiKey config.
  /// </summary>
  private bool ValidateDebugAccess()
  {
    if (_environment.IsDevelopment() && Extensions.DebugEndpointAvailability.KeylessDevelopmentAllowed)
    {
      return true;
    }

    var configuredKey = _configuration["Debug:ApiKey"];
    if (string.IsNullOrEmpty(configuredKey))
    {
      _logger.LogWarning("Debug:ApiKey not configured — debug token endpoint is disabled in {Environment}",
        _environment.EnvironmentName);
      return false;
    }

    var providedKey = HttpContext.Request.Headers["X-Debug-Key"].FirstOrDefault();
    return !string.IsNullOrEmpty(providedKey) &&
           string.Equals(providedKey, configuredKey, StringComparison.Ordinal);
  }
}
