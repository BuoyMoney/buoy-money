using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;

using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.ValueObjects;

namespace BuoyAI.API.Controllers;

[Authorize]
public class UserController : BaseController
{
  private readonly IAuthService _authService;
  private readonly IUserRepository _userRepository;
  private readonly IBudgetRepository _budgetRepository;
  private readonly IBudgetPeriodService _budgetPeriodService;
  private readonly ILogger<UserController> _logger;

  public UserController(IAuthService authService, IUserRepository userRepository, ILogger<UserController> logger,
    IBudgetRepository budgetRepository,
    IBudgetPeriodService budgetPeriodService)
  {
    _authService = authService;
    _userRepository = userRepository;
    _logger = logger;
      _budgetRepository = budgetRepository;
    _budgetPeriodService = budgetPeriodService;
  }

  [HttpGet("profile")]
  public async Task<IActionResult> GetProfile()
  {
    var userId = GetCurrentUserId();
    var profile = await _authService.GetUserProfileAsync(userId);

    return HandleServiceResponse(profile, "User profile not found");
  }

  /// <summary>
  /// Update the current user's profile (name, primary currency). The Flutter client
  /// calls this from the profile page; the endpoint was previously missing, so saving
  /// a name returned 404. Email changes are intentionally not handled here (they
  /// require re-verification). A currency-only update may omit the name fields.
  /// </summary>
  [HttpPut("profile")]
  public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
  {
    var userId = GetCurrentUserId();
    var user = await _userRepository.GetByIdAsync(userId);
    if (user == null)
    {
      return NotFound(new { message = "User not found" });
    }

    var firstName = request.FirstName?.Trim();
    var lastName = request.LastName?.Trim();
    var updatingName = request.FirstName != null || request.LastName != null;
    // Name validation only applies when the request is editing the name; a
    // currency-only update must not force the client to resend the name.
    if (updatingName && string.IsNullOrEmpty(firstName))
    {
      return BadRequest(new { message = "First name is required." });
    }

    string? normalizedCurrency = null;
    if (request.Currency != null)
    {
      normalizedCurrency = CurrencyCodes.Normalize(request.Currency);
      if (normalizedCurrency == null)
      {
        return BadRequest(new { message = "Unsupported currency code." });
      }
    }

    if (request.WeekStartDay is < 0 or > 6)
    {
      return BadRequest(new { message = "Week start day must be 0 (Sunday) through 6 (Saturday)." });
    }

    if (!updatingName && normalizedCurrency == null && request.WeekStartDay == null)
    {
      return BadRequest(new { message = "Nothing to update." });
    }

    if (updatingName)
    {
      user.FirstName = firstName!;
      // Last name is optional: Apple / "Hide My Email" sign-ins provide no name at
      // all, and some users go by a single name. Only overwrite when one is sent.
      if (lastName != null)
      {
        user.LastName = lastName;
      }
    }

    if (normalizedCurrency != null)
    {
      user.Currency = normalizedCurrency;
      // An explicit choice is final: the first-link default must never override it.
      user.CurrencyIsUserSet = true;
    }

    var weekStartChanged = request.WeekStartDay != null && user.WeekStartDay != request.WeekStartDay;
    if (request.WeekStartDay != null)
    {
      user.WeekStartDay = request.WeekStartDay;
    }

    user.UpdatedAt = DateTime.UtcNow;
    await _userRepository.UpdateAsync(user);

    // Changing the week anchor re-bases the user's ACTIVE weekly/bi-weekly budgets
    // immediately (#941) — ongoing budgets otherwise roll +7/+14 days from their old
    // anchor forever and the setting would appear to do nothing. Monthly and longer
    // periods are unaffected. Boundary shift is the user's explicit, opted-in action.
    if (weekStartChanged)
    {
      var weekStart = (DayOfWeek)request.WeekStartDay!.Value;
      foreach (var budget in await _budgetRepository.GetByUserIdAsync(userId))
      {
        if (!budget.IsActive ||
            (budget.Period != BudgetPeriod.Weekly && budget.Period != BudgetPeriod.BiWeekly))
        {
          continue;
        }
        var (start, end) = _budgetPeriodService.CalculateCurrentPeriod(budget.Period, weekStart: weekStart);
        budget.CurrentPeriodStart = start;
        budget.CurrentPeriodEnd = end;
        await _budgetRepository.UpdateAsync(budget);
      }
    }

    _logger.LogInformation("Updated profile for user {UserId}", userId);

    var profile = await _authService.GetUserProfileAsync(userId);
    return HandleServiceResponse(profile, "User profile not found");
  }

  // ── Avatar (uploaded profile picture, stored in DB) ──────────────────────

  private static readonly HashSet<string> AllowedAvatarTypes =
    new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };

  // Client resizes to ~256px before upload; this is a safety cap on the raw bytes.
  private const long MaxAvatarBytes = 512 * 1024;

  /// <summary>Upload/replace the current user's profile picture. The client resizes first.</summary>
  [HttpPost("avatar")]
  public async Task<IActionResult> UploadAvatar(IFormFile? file)
  {
    var userId = GetCurrentUserId();
    if (file == null || file.Length == 0)
    {
      return BadRequest(new { message = "No image provided." });
    }
    if (file.Length > MaxAvatarBytes)
    {
      return BadRequest(new { message = "Image too large (max 512KB). Try a smaller photo." });
    }
    if (!AllowedAvatarTypes.Contains(file.ContentType))
    {
      return BadRequest(new { message = "Unsupported image type. Use JPEG, PNG, or WebP." });
    }

    // Security (#615 QE): the declared ContentType is attacker-controlled. Verify the
    // actual magic bytes match so a renamed executable can't be stored + served as an image.
    var header = new byte[FileSignatureValidator.HeaderSize];
    int read;
    using (var probe = file.OpenReadStream())
    {
      read = await probe.ReadAsync(header.AsMemory(0, header.Length));
    }
    if (!FileSignatureValidator.IsContentConsistent(header.AsSpan(0, read), file.ContentType.ToLowerInvariant()))
    {
      _logger.LogWarning("User {UserId} avatar upload rejected: content does not match declared type {ContentType}",
        userId, file.ContentType);
      return BadRequest(new { message = "That file isn't a valid image. Use a real JPEG, PNG, or WebP." });
    }

    var user = await _userRepository.GetByIdAsync(userId);
    if (user == null)
    {
      return NotFound(new { message = "User not found" });
    }

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    user.AvatarImage = ms.ToArray();
    user.AvatarContentType = file.ContentType;
    user.AvatarUpdatedAt = DateTime.UtcNow;
    user.UpdatedAt = DateTime.UtcNow;
    await _userRepository.UpdateAsync(user);
    _logger.LogInformation("Updated avatar for user {UserId} ({Bytes} bytes)", userId, user.AvatarImage.Length);

    var profile = await _authService.GetUserProfileAsync(userId);
    return HandleServiceResponse(profile, "User profile not found");
  }

  /// <summary>
  /// Serve a user's uploaded avatar image. Public (like Gravatar) so an &lt;img&gt; can
  /// load it without an auth header; avatars are not sensitive. 404 when none set.
  /// </summary>
  [AllowAnonymous]
  [HttpGet("avatar/{userId:int}")]
  public async Task<IActionResult> GetAvatar(int userId)
  {
    var user = await _userRepository.GetByIdAsync(userId);
    if (user?.AvatarImage == null || string.IsNullOrEmpty(user.AvatarContentType))
    {
      return NotFound();
    }
    // Cache-busted by the ?v=AvatarUpdatedAt the client appends, so a long TTL is safe.
    Response.Headers.CacheControl = "public, max-age=86400";
    return File(user.AvatarImage, user.AvatarContentType);
  }

  /// <summary>Remove the current user's uploaded avatar (revert to initials).</summary>
  [HttpDelete("avatar")]
  public async Task<IActionResult> DeleteAvatar()
  {
    var userId = GetCurrentUserId();
    var user = await _userRepository.GetByIdAsync(userId);
    if (user == null)
    {
      return NotFound(new { message = "User not found" });
    }
    user.AvatarImage = null;
    user.AvatarContentType = null;
    user.AvatarUpdatedAt = null;
    user.UpdatedAt = DateTime.UtcNow;
    await _userRepository.UpdateAsync(user);

    var profile = await _authService.GetUserProfileAsync(userId);
    return HandleServiceResponse(profile, "User profile not found");
  }

  /// <summary>
  /// Get user's budget-related settings
  /// </summary>
  [HttpGet("settings/budget")]
  public async Task<IActionResult> GetBudgetSettings()
  {
    var userId = GetCurrentUserId();
    var user = await _userRepository.GetByIdAsync(userId);

    if (user == null)
    {
      return NotFound(new { message = "User not found" });
    }

    return Ok(new BudgetSettingsDto
    {
      ExcludePendingFromBudget = user.ExcludePendingFromBudget,
      ReattributeOnAccountAssociationChange = user.ReattributeOnAccountAssociationChange
    });
  }

  /// <summary>
  /// Update user's budget-related settings
  /// </summary>
  [HttpPut("settings/budget")]
  public async Task<IActionResult> UpdateBudgetSettings([FromBody] UpdateBudgetSettingsRequest request)
  {
    var userId = GetCurrentUserId();
    var user = await _userRepository.GetByIdAsync(userId);

    if (user == null)
    {
      return NotFound(new { message = "User not found" });
    }

    user.ExcludePendingFromBudget = request.ExcludePendingFromBudget;
    user.ReattributeOnAccountAssociationChange = request.ReattributeOnAccountAssociationChange;
    await _userRepository.UpdateAsync(user);

    _logger.LogInformation(
      "User {UserId} updated budget settings: ExcludePendingFromBudget={ExcludePending}, ReattributeOnAssoc={Reattribute}",
      userId, request.ExcludePendingFromBudget, request.ReattributeOnAccountAssociationChange);

    return Ok(new BudgetSettingsDto
    {
      ExcludePendingFromBudget = user.ExcludePendingFromBudget,
      ReattributeOnAccountAssociationChange = user.ReattributeOnAccountAssociationChange
    });
  }

  /// <summary>
  /// Get user's subscription prompt settings
  /// </summary>
  [HttpGet("settings/subscription-prompt")]
  public async Task<IActionResult> GetSubscriptionPromptSettings()
  {
    var userId = GetCurrentUserId();
    var user = await _userRepository.GetByIdAsync(userId);

    if (user == null)
    {
      return NotFound(new { message = "User not found" });
    }

    var categories = new List<string>();
    if (!string.IsNullOrEmpty(user.SubscriptionPromptCategories))
    {
      try
      {
        categories = System.Text.Json.JsonSerializer.Deserialize<List<string>>(user.SubscriptionPromptCategories) ?? new List<string>();
      }
      catch
      {
        // If JSON is invalid, return empty list
        categories = new List<string>();
      }
    }

    return Ok(new SubscriptionPromptSettingsDto
    {
      PromptCategories = categories
    });
  }

  /// <summary>
  /// Update user's subscription prompt settings
  /// </summary>
  [HttpPut("settings/subscription-prompt")]
  public async Task<IActionResult> UpdateSubscriptionPromptSettings([FromBody] UpdateSubscriptionPromptSettingsRequest request)
  {
    var userId = GetCurrentUserId();
    var user = await _userRepository.GetByIdAsync(userId);

    if (user == null)
    {
      return NotFound(new { message = "User not found" });
    }

    user.SubscriptionPromptCategories = System.Text.Json.JsonSerializer.Serialize(request.PromptCategories);
    await _userRepository.UpdateAsync(user);

    _logger.LogInformation("User {UserId} updated subscription prompt settings: {Categories}",
      userId, user.SubscriptionPromptCategories);

    return Ok(new SubscriptionPromptSettingsDto
    {
      PromptCategories = request.PromptCategories
    });
  }

  /// <summary>
  /// Get user's AI-related settings including memory opt-in and interaction mode
  /// </summary>
  /// <summary>
  /// Whether the user has granted permission to share their data with third-party AI
  /// (Google Gemini). App Store 5.1.2(i). Null grant = not granted, which includes a grant
  /// given under an older disclosure (see AiConsent.CurrentVersion).
  /// </summary>
  [HttpGet("ai-consent")]
  public async Task<IActionResult> GetAiConsent()
  {
    var user = await _userRepository.GetByIdAsync(GetCurrentUserId());
    if (user == null) return NotFound(new { message = "User not found" });
    return Ok(AiConsentBody(user));
  }

  private static object AiConsentBody(User user) => new
  {
    aiConsentGrantedAt = AiConsent.VisibleGrantedAt(user),
    aiConsentWithdrawnAt = user.AiConsentWithdrawnAt,
    aiConsentVersion = user.AiConsentVersion,
    currentAiConsentVersion = AiConsent.CurrentVersion,
  };

  /// <summary>
  /// Grant or withdraw AI data-sharing consent. Withdrawing is always allowed
  /// (5.1.1(ii): an easily accessible way to withdraw consent) and takes effect on
  /// the next AI request; nothing already generated is deleted by this call.
  /// </summary>
  [HttpPut("ai-consent")]
  public async Task<IActionResult> UpdateAiConsent([FromBody] UpdateAiConsentRequest request)
  {
    var userId = GetCurrentUserId();
    var user = await _userRepository.GetByIdAsync(userId);
    if (user == null) return NotFound(new { message = "User not found" });

    var now = DateTime.UtcNow;
    user.AiConsentGrantedAt = request.Granted ? now : null;
    user.AiConsentWithdrawnAt = request.Granted ? null : now;
    // The grant is recorded against the disclosure in force now. Every client that can call
    // this shows the consent sheet first, so the current version is what the user agreed to.
    user.AiConsentVersion = request.Granted ? AiConsent.CurrentVersion : user.AiConsentVersion;
    user.UpdatedAt = now;
    await _userRepository.UpdateAsync(user);
    _logger.LogInformation("AI consent {Action} for user {UserId} (version {Version})",
      request.Granted ? "granted" : "withdrawn", userId, AiConsent.CurrentVersion);
    return Ok(AiConsentBody(user));
  }

  [HttpGet("settings/ai")]
  public async Task<IActionResult> GetAiSettings()
  {
    var userId = GetCurrentUserId();
    var user = await _userRepository.GetByIdAsync(userId);

    if (user == null)
    {
      return NotFound(new { message = "User not found" });
    }

    return Ok(new AiSettingsDto
    {
      AiMemoryEnabled = user.AiMemoryEnabled,
      AiInteractionMode = user.AiInteractionMode.ToString(),
      AiAutonomyLevel = user.AiAutonomyLevel.ToString()
    });
  }

  /// <summary>
  /// Update user's AI-related settings
  /// </summary>
  [HttpPut("settings/ai")]
  public async Task<IActionResult> UpdateAiSettings([FromBody] UpdateAiSettingsRequest request)
  {
    var userId = GetCurrentUserId();
    var user = await _userRepository.GetByIdAsync(userId);

    if (user == null)
    {
      return NotFound(new { message = "User not found" });
    }

    if (request.AiMemoryEnabled.HasValue)
    {
      user.AiMemoryEnabled = request.AiMemoryEnabled.Value;
    }

    if (!string.IsNullOrEmpty(request.AiInteractionMode) &&
        Enum.TryParse<BuoyAI.Core.Enums.AIInteractionMode>(request.AiInteractionMode, true, out var mode))
    {
      user.AiInteractionMode = mode;
    }

    if (!string.IsNullOrEmpty(request.AiAutonomyLevel) &&
        Enum.TryParse<BuoyAI.Core.Enums.AIAutonomyLevel>(request.AiAutonomyLevel, true, out var autonomy))
    {
      user.AiAutonomyLevel = autonomy;
    }

    await _userRepository.UpdateAsync(user);

    _logger.LogInformation("User {UserId} updated AI settings: MemoryEnabled={MemoryEnabled}, InteractionMode={Mode}, AutonomyLevel={Autonomy}",
      userId, user.AiMemoryEnabled, user.AiInteractionMode, user.AiAutonomyLevel);

    return Ok(new AiSettingsDto
    {
      AiMemoryEnabled = user.AiMemoryEnabled,
      AiInteractionMode = user.AiInteractionMode.ToString(),
      AiAutonomyLevel = user.AiAutonomyLevel.ToString()
    });
  }

  /// <summary>
  /// Permanently delete the current user's account.
  /// This action is irreversible and will delete all user data.
  /// </summary>
  [HttpDelete("account")]
  public async Task<IActionResult> DeleteAccount([FromBody] DeleteAccountRequest? request = null)
  {
    var userId = GetCurrentUserId();
    _logger.LogInformation("User {UserId} requested account deletion", userId);

    var success = await _authService.DeleteAccountAsync(userId, request?.Password);

    if (!success)
    {
      return BadRequest(new { message = "Account deletion failed. If you have a password, please provide it for verification." });
    }

    _logger.LogInformation("Successfully deleted account for user {UserId}", userId);
    return Ok(new { message = "Your account has been permanently deleted." });
  }
}

public class BudgetSettingsDto
{
  public bool ExcludePendingFromBudget { get; set; }
  public bool ReattributeOnAccountAssociationChange { get; set; }
}

public class UpdateBudgetSettingsRequest
{
  public bool ExcludePendingFromBudget { get; set; }
  public bool ReattributeOnAccountAssociationChange { get; set; }
}

public class DeleteAccountRequest
{
  /// <summary>
  /// Password for verification (required for non-Google users)
  /// </summary>
  public string? Password { get; set; }
}

/// <summary>
/// Settings for subscription tracking prompts when categorizing transactions
/// </summary>
public class SubscriptionPromptSettingsDto
{
  /// <summary>
  /// List of category identifiers that trigger subscription tracking prompt.
  /// Built-in categories as string numbers (e.g., "13"), custom as "custom_123".
  /// </summary>
  public List<string> PromptCategories { get; set; } = new();
}

public class UpdateSubscriptionPromptSettingsRequest
{
  /// <summary>
  /// List of category identifiers to prompt for subscription tracking.
  /// Built-in categories as string numbers (e.g., "13"), custom as "custom_123".
  /// </summary>
  public List<string> PromptCategories { get; set; } = new();
}

/// <summary>
/// AI-related settings for the user
/// </summary>
public class AiSettingsDto
{
  /// <summary>
  /// Whether AI memory feature is enabled for this user.
  /// When enabled, past conversations can be used to provide contextual responses.
  /// </summary>
  public bool AiMemoryEnabled { get; set; }

  /// <summary>
  /// AI interaction mode preference (Quick or Clarifying)
  /// </summary>
  public string AiInteractionMode { get; set; } = "Quick";

  /// <summary>
  /// AI Control Spectrum / autonomy level: Manual, Coach, Assistant, or Manager (issue #412).
  /// </summary>
  public string AiAutonomyLevel { get; set; } = "Coach";
}

/// <summary>
/// Request to update AI settings
/// </summary>
public class UpdateAiSettingsRequest
{
  /// <summary>
  /// Set to enable/disable AI memory feature (null to keep unchanged)
  /// </summary>
  public bool? AiMemoryEnabled { get; set; }

  /// <summary>
  /// AI interaction mode preference: "Quick" or "Clarifying" (null to keep unchanged)
  /// </summary>
  public string? AiInteractionMode { get; set; }

  /// <summary>
  /// AI Control Spectrum / autonomy level: "Manual", "Coach", "Assistant", "Manager"
  /// (null to keep unchanged).
  /// </summary>
  public string? AiAutonomyLevel { get; set; }
}
