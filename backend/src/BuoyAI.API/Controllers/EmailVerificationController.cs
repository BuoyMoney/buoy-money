using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Email-address verification. A 6-digit code is sent to the user's email (reusing the
/// two-factor code system, which also confirms our email-delivered MFA fallback works).
/// A verified email is required before protected operations such as connecting a bank.
/// </summary>
[Authorize]
public class EmailVerificationController : BaseController
{
    /// <summary>Two-factor code purpose used for email-address verification.</summary>
    public const string Purpose = "email_verification";

    private readonly ITwoFactorService _twoFactorService;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<EmailVerificationController> _logger;

    public EmailVerificationController(
        ITwoFactorService twoFactorService,
        IUserRepository userRepository,
        ILogger<EmailVerificationController> logger)
    {
        _twoFactorService = twoFactorService;
        _userRepository = userRepository;
        _logger = logger;
    }

    /// <summary>Whether the current user's email is already verified.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var user = await _userRepository.GetByIdAsync(GetCurrentUserId());
        return Ok(new { isEmailVerified = user?.IsEmailVerified ?? false });
    }

    /// <summary>Sends a fresh verification code to the user's email.</summary>
    [HttpPost("send")]
    public async Task<IActionResult> SendCode()
    {
        var userId = GetCurrentUserId();
        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null)
        {
            return NotFound(new { message = "User not found" });
        }

        if (user.IsEmailVerified)
        {
            return Ok(new { message = "Email already verified", isEmailVerified = true });
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();
        await _twoFactorService.GenerateAndSendCodeAsync(userId, Purpose, ipAddress, userAgent);

        _logger.LogInformation("Sent email verification code to user {UserId}", userId);
        return Ok(new { message = "Verification code sent" });
    }

    /// <summary>Confirms the emailed code and marks the email verified.</summary>
    [HttpPost("confirm")]
    public async Task<IActionResult> ConfirmCode([FromBody] ConfirmEmailVerificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new { message = "Verification code is required" });
        }

        var userId = GetCurrentUserId();
        var isValid = await _twoFactorService.VerifyCodeAsync(userId, request.Code.Trim(), Purpose);
        if (!isValid)
        {
            return BadRequest(new { message = "Invalid or expired verification code" });
        }

        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null)
        {
            return NotFound(new { message = "User not found" });
        }

        var changed = false;
        if (!user.IsEmailVerified)
        {
            user.IsEmailVerified = true;
            user.EmailVerifiedAt = DateTime.UtcNow;
            changed = true;
        }

        // Verifying the email also makes email-code a usable MFA method (the user just
        // proved they receive codes). This unifies the dashboard "verify email" flow and
        // the Settings "Email Code" MFA setup: completing either satisfies both. We never
        // downgrade a stronger primary (passkey/TOTP) — only fill in email when there's
        // no MFA configured yet.
        if (user.MfaType == MfaType.None)
        {
            user.MfaType = MfaType.Email;
            changed = true;
        }
        if (!user.HasMfaEnabled)
        {
            user.HasMfaEnabled = true;
            user.MfaEnabledAt ??= DateTime.UtcNow;
            changed = true;
        }

        if (changed)
        {
            user.UpdatedAt = DateTime.UtcNow;
            await _userRepository.UpdateAsync(user);
            _logger.LogInformation("Email verified (and email MFA ensured) for user {UserId}", userId);
        }

        return Ok(new { message = "Email verified", isEmailVerified = true });
    }
}
