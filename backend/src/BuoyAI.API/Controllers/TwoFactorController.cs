using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.API.Attributes;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Controller for managing two-factor authentication
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TwoFactorController : BaseController
{
    private readonly ITwoFactorService _twoFactorService;
    private readonly IAuthService _authService;
    private readonly IDeviceTrustService _deviceTrustService;
    private readonly IMfaPreferencesService _mfaPreferencesService;
    private readonly IEmailService _emailService;
    private readonly IIpGeolocationService _geolocationService;
    private readonly ILogger<TwoFactorController> _logger;

    public TwoFactorController(
        ITwoFactorService twoFactorService, 
        IAuthService authService,
        IDeviceTrustService deviceTrustService,
        IMfaPreferencesService mfaPreferencesService,
        IEmailService emailService,
        IIpGeolocationService geolocationService,
        ILogger<TwoFactorController> logger)
    {
        _twoFactorService = twoFactorService;
        _authService = authService;
        _deviceTrustService = deviceTrustService;
        _mfaPreferencesService = mfaPreferencesService;
        _emailService = emailService;
        _geolocationService = geolocationService;
        _logger = logger;
    }

    /// <summary>
    /// Send a 2FA code via email
    /// </summary>
    [HttpPost("send-code")]
    public async Task<IActionResult> SendCode([FromBody] SendTwoFactorCodeRequest request)
    {
        var userId = GetCurrentUserId();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers["User-Agent"].ToString();

        await _twoFactorService.GenerateAndSendCodeAsync(userId, request.Purpose, ipAddress, userAgent);

        return Ok(new { Message = "2FA code sent successfully" });
    }

    /// <summary>
    /// Verify a 2FA code
    /// </summary>
    [HttpPost("verify-code")]
    public async Task<IActionResult> VerifyCode([FromBody] VerifyTwoFactorCodeRequest request)
    {
        var userId = GetCurrentUserId();
        var isValid = await _twoFactorService.VerifyCodeAsync(userId, request.Code, request.Purpose);

        if (isValid)
        {
            return Ok(new { Message = "Code verified successfully", IsValid = true });
        }

        return BadRequest(new { Message = "Invalid or expired code", IsValid = false });
    }

    /// <summary>
    /// Enable email-based MFA for the user
    /// </summary>
    [HttpPost("enable-email")]
    public async Task<IActionResult> EnableEmailMfa()
    {
        var userId = GetCurrentUserId();
        await _twoFactorService.EnableEmailMfaAsync(userId);

        return Ok(new { Message = "Email MFA enabled successfully" });
    }

    /// <summary>
    /// Initiate TOTP setup
    /// </summary>
    [HttpPost("setup-totp")]
    public async Task<IActionResult> SetupTotp()
    {
        var userId = GetCurrentUserId();
        var setupInfo = await _twoFactorService.EnableTotpMfaAsync(userId);

        return Ok(setupInfo);
    }

    /// <summary>
    /// Complete TOTP setup by verifying a code
    /// </summary>
    [HttpPost("verify-totp")]
    public async Task<IActionResult> VerifyTotp([FromBody] VerifyTotpRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var recoveryCodes = await _twoFactorService.CompleteTotpSetupAsync(userId, request.Code);

            return Ok(new {
                Message = "TOTP setup completed successfully",
                RecoveryCodes = recoveryCodes
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid TOTP verification attempt for user {UserId}", GetCurrentUserId());
            return BadRequest(new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Generate new recovery codes
    /// </summary>
    [HttpPost("recovery-codes")]
    public async Task<IActionResult> GenerateRecoveryCodes()
    {
        var userId = GetCurrentUserId();
        var recoveryCodes = await _twoFactorService.GenerateRecoveryCodesAsync(userId);

        return Ok(new { RecoveryCodes = recoveryCodes });
    }

    /// <summary>
    /// Check if user needs to upgrade their MFA
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetMfaStatus()
    {
        var userId = GetCurrentUserId();
        var requiresUpgrade = await _twoFactorService.RequiresUpgradeAsync(userId);
        var canAccessPlaid = await _twoFactorService.CanAccessPlaidFeaturesAsync(userId);
        var mfaDetails = await _twoFactorService.GetUserMfaDetailsAsync(userId);

        return Ok(new {
            RequiresUpgrade = requiresUpgrade,
            CanAccessPlaidFeatures = canAccessPlaid,
            CurrentMfaType = mfaDetails.MfaType,
            HasPasskeyRegistered = mfaDetails.HasPasskeyRegistered,
            HasMfaEnabled = mfaDetails.HasMfaEnabled,
            MfaEnabledAt = mfaDetails.MfaEnabledAt,
            MfaGracePeriodEndsAt = mfaDetails.MfaGracePeriodEndsAt
        });
    }

    /// <summary>
    /// Disable ALL multi-factor authentication for the user (authenticator, email MFA,
    /// and passkeys). Gated behind step-up MFA: the user must re-verify a current factor
    /// (handled by <see cref="RequiresMfaAttribute"/>) before their protection is removed,
    /// so a hijacked session can't silently strip MFA.
    /// </summary>
    [HttpDelete("disable")]
    [RequiresMfa(ProtectedOperation.ChangeMfaSettings)]
    public async Task<IActionResult> DisableMfa()
    {
        var userId = GetCurrentUserId();
        try
        {
            await _twoFactorService.DisableMfaAsync(userId);
            return Ok(new { Message = "MFA disabled successfully" });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Disable MFA rejected for user {UserId}", userId);
            return BadRequest(new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Invalidate all active 2FA codes for the user
    /// </summary>
    [HttpPost("invalidate-codes")]
    public async Task<IActionResult> InvalidateCodes([FromBody] InvalidateCodesRequest? request = null)
    {
        var userId = GetCurrentUserId();
        await _twoFactorService.InvalidateCodesAsync(userId, request?.Purpose);

        return Ok(new { Message = "Codes invalidated successfully" });
    }

    #region Passkey Endpoints

    /// <summary>
    /// Initiate passkey registration for the authenticated user
    /// </summary>
    [HttpPost("passkey/register/begin")]
    public async Task<IActionResult> BeginPasskeyRegistration([FromBody] PasskeyRegistrationInitRequest request)
    {
        var userId = GetCurrentUserId();
        var initDto = new PasskeyRegistrationInitDto
        {
            DisplayName = request.DisplayName,
            DeviceName = request.DeviceName
        };

        var options = await _twoFactorService.InitiatePasskeyRegistrationAsync(userId, initDto);

        return Ok(options);
    }

    /// <summary>
    /// Complete passkey registration with the credential response
    /// </summary>
    [HttpPost("passkey/register/complete")]
    public async Task<IActionResult> CompletePasskeyRegistration([FromBody] PasskeyRegistrationCompleteRequest request)
    {
        var userId = GetCurrentUserId();
        var completeDto = new PasskeyRegistrationCompleteDto
        {
            CredentialResponse = request.CredentialResponse,
            DeviceName = request.DeviceName
        };

        await _twoFactorService.CompletePasskeyRegistrationAsync(userId, completeDto);

        return Ok(new { Message = "Passkey registered successfully" });
    }

    /// <summary>
    /// Initiate passkey authentication
    /// </summary>
    [HttpPost("passkey/authenticate/begin")]
    [AllowAnonymous]
    public async Task<IActionResult> BeginPasskeyAuthentication([FromBody] PasskeyAuthenticationInitRequest? request = null)
    {
        var initDto = new PasskeyAuthenticationInitDto
        {
            Username = request?.Username
        };

        var options = await _twoFactorService.InitiatePasskeyAuthenticationAsync(initDto);

        return Ok(options);
    }

    /// <summary>
    /// Complete passkey authentication and return JWT token with device trust handling
    /// </summary>
    [HttpPost("passkey/authenticate/complete")]
    [AllowAnonymous]
    public async Task<IActionResult> CompletePasskeyAuthentication([FromBody] PasskeyAuthenticationCompleteRequest request)
    {
        var completeDto = new PasskeyAuthenticationCompleteDto
        {
            AssertionResponse = request.AssertionResponse
        };

        var user = await _twoFactorService.CompletePasskeyAuthenticationAsync(completeDto);

        // Generate JWT token for authenticated user with subscription tier for rate limiting
        var token = _authService.GenerateJwtTokenWithClaims(user.Id, user.Username, _authService.ResolveTierClaim(user));
        var expiresAt = DateTime.UtcNow.AddHours(24);

        // Get device information from request context
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers["User-Agent"].ToString();

        var deviceInfo = new DeviceInfoDto
        {
            IpAddress = ipAddress,
            UserAgent = userAgent,
            DeviceFingerprint = request.DeviceFingerprint,
            RememberDevice = request.RememberDevice
        };

        // Register or update trusted device if requested
        if (request.RememberDevice)
        {
            await _deviceTrustService.RegisterTrustedDeviceAsync(user.Id, deviceInfo);

            // Check if user wants new device notifications
            var mfaPreferences = await _mfaPreferencesService.GetPreferencesAsync(user.Id);
            if (mfaPreferences.NotifyOnNewDevice)
            {
                try
                {
                    // Get geolocation for the notification
                    var location = "Unknown Location";
                    if (!string.IsNullOrEmpty(deviceInfo.IpAddress))
                    {
                        var geoLocation = await _geolocationService.GetLocationAsync(deviceInfo.IpAddress);
                        if (geoLocation != null)
                        {
                            location = $"{geoLocation.City}, {geoLocation.Region}, {geoLocation.Country}";
                        }
                    }

                    var deviceName = !string.IsNullOrEmpty(deviceInfo.UserAgent) ? deviceInfo.UserAgent : "Unknown Device";
                    await _emailService.SendNewDeviceLoginAsync(
                        user.Email,
                        user.FirstName ?? "User",
                        deviceName,
                        location,
                        deviceInfo.IpAddress ?? "Unknown IP",
                        DateTime.UtcNow
                    );

                    _logger.LogInformation("Sent new device notification email to user {UserId}", user.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send new device notification email to user {UserId}", user.Id);
                    // Don't fail the login if email fails
                }
            }
        }

        // Log successful passkey authentication
        var evaluation = await _deviceTrustService.EvaluateDeviceTrustAsync(user.Id, deviceInfo);
        await _deviceTrustService.LogLoginAttemptAsync(
            user.Id,
            deviceInfo,
            success: true,
            mfaChallenged: true,
            mfaSuccess: true,
            riskScore: evaluation.RiskScore);

        var loginResponse = new EnhancedLoginResponse
        {
            RequiresMfa = false,
            Token = token,
            User = new UserProfileDto
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                FirstName = user.FirstName ?? "User",
                LastName = user.LastName,
                Currency = user.Currency,
                ProfilePictureUrl = user.ProfilePictureUrl,
                IsGoogleUser = user.IsGoogleUser,
                IsAppleUser = user.IsAppleUser,
                HasPassword = !string.IsNullOrEmpty(user.PasswordHash),
                SubscriptionTier = _authService.ResolveTierClaim(user)?.ToString() ?? (user.IsTrialActive ? "Trial" : "Free"),
                MfaEnabled = user.HasMfaEnabled,
                TrialEndsAt = user.TrialEndDate,
                CreatedAt = user.CreatedAt,
                FamilyId = user.FamilyId,
            },
            ExpiresAt = expiresAt,
            MfaReasons = new List<string>()
        };

        return Ok(loginResponse);
    }

    /// <summary>
    /// Get all registered passkeys for the authenticated user
    /// </summary>
    [HttpGet("passkey/list")]
    public async Task<IActionResult> GetUserPasskeys()
    {
        var userId = GetCurrentUserId();
        var passkeys = await _twoFactorService.GetUserPasskeysAsync(userId);

        return Ok(passkeys);
    }

    /// <summary>
    /// Remove a registered passkey
    /// </summary>
    [HttpDelete("passkey/{passkeyId}")]
    [RequiresMfa(ProtectedOperation.ManagePasskeys)]
    public async Task<IActionResult> RemovePasskey(int passkeyId)
    {
        var userId = GetCurrentUserId();
        await _twoFactorService.RemovePasskeyAsync(userId, passkeyId);

        return Ok(new { Message = "Passkey removed successfully" });
    }

    /// <summary>
    /// Update a passkey's name
    /// </summary>
    [HttpPut("passkey/{passkeyId}")]
    public async Task<IActionResult> UpdatePasskey(int passkeyId, [FromBody] PasskeyUpdateRequest request)
    {
        var userId = GetCurrentUserId();
        var updateDto = new PasskeyUpdateDto
        {
            Name = request.Name
        };

        await _twoFactorService.UpdatePasskeyAsync(userId, passkeyId, updateDto);

        return Ok(new { Message = "Passkey updated successfully" });
    }

    #endregion
}

/// <summary>
/// Request model for sending 2FA codes
/// </summary>
public class SendTwoFactorCodeRequest
{
    public string Purpose { get; set; } = string.Empty;
}

/// <summary>
/// Request model for verifying 2FA codes
/// </summary>
public class VerifyTwoFactorCodeRequest
{
    public string Code { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
}

/// <summary>
/// Request model for TOTP verification
/// </summary>
public class VerifyTotpRequest
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// Request model for invalidating codes
/// </summary>
public class InvalidateCodesRequest
{
    public string? Purpose { get; set; }
}

/// <summary>
/// Request model for initiating passkey registration
/// </summary>
public class PasskeyRegistrationInitRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
}

/// <summary>
/// Request model for completing passkey registration
/// </summary>
public class PasskeyRegistrationCompleteRequest
{
    public string CredentialResponse { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
}

/// <summary>
/// Request model for initiating passkey authentication
/// </summary>
public class PasskeyAuthenticationInitRequest
{
    public string? Username { get; set; }
}

/// <summary>
/// Request model for completing passkey authentication
/// </summary>
public class PasskeyAuthenticationCompleteRequest
{
    public string AssertionResponse { get; set; } = string.Empty;
    public string? DeviceFingerprint { get; set; }
    public bool RememberDevice { get; set; }
}

/// <summary>
/// Request model for updating a passkey
/// </summary>
public class PasskeyUpdateRequest
{
    public string Name { get; set; } = string.Empty;
}
