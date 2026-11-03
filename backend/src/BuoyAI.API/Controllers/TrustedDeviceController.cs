using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Controller for managing trusted devices
/// </summary>
[ApiController]
[Route("api/trusted-devices")]
[Authorize]
public class TrustedDeviceController : BaseController
{
    private readonly IDeviceTrustService _deviceTrustService;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly ILogger<TrustedDeviceController> _logger;

    public TrustedDeviceController(
        IDeviceTrustService deviceTrustService,
        IRefreshTokenService refreshTokenService,
        ILogger<TrustedDeviceController> logger)
    {
        _deviceTrustService = deviceTrustService;
        _refreshTokenService = refreshTokenService;
        _logger = logger;
    }

    /// <summary>
    /// Get all trusted devices for the current user
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetTrustedDevices()
    {
        var userId = GetCurrentUserId();
        var devices = await _deviceTrustService.GetTrustedDevicesAsync(userId);
        return Ok(devices);
    }

    /// <summary>
    /// Revoke trust for a specific device
    /// </summary>
    [HttpDelete("{deviceId}")]
    public async Task<IActionResult> RevokeTrustedDevice(int deviceId)
    {
        var userId = GetCurrentUserId();
        await _deviceTrustService.RevokeTrustedDeviceAsync(userId, deviceId);

        _logger.LogInformation("User {UserId} revoked trusted device {DeviceId}", userId, deviceId);

        return Ok(new { message = "Device trust revoked successfully" });
    }

    /// <summary>
    /// Revoke trust for all devices AND end every active session ("sign out everywhere").
    /// Revoking device trust alone would only force MFA on the next login; revoking the
    /// refresh tokens is what actually signs other devices out — their next token rotation
    /// fails, so a lost/stolen device (or a stolen refresh token) can no longer stay signed in.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> RevokeAllTrustedDevices()
    {
        var userId = GetCurrentUserId();
        await _deviceTrustService.RevokeAllTrustedDevicesAsync(userId);
        var revokedSessions = await _refreshTokenService.RevokeAllForUserAsync(userId);

        _logger.LogInformation(
            "User {UserId} revoked all trusted devices and {SessionCount} sessions", userId, revokedSessions);

        return Ok(new { message = "All device trusts revoked successfully" });
    }

    /// <summary>
    /// Evaluate current device trust status
    /// </summary>
    [HttpPost("evaluate")]
    public async Task<IActionResult> EvaluateDeviceTrust([FromBody] DeviceInfoDto deviceInfo)
    {
        var userId = GetCurrentUserId();

        // Enhance device info with request context if not provided
        if (string.IsNullOrEmpty(deviceInfo.IpAddress))
        {
            deviceInfo.IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        }
        if (string.IsNullOrEmpty(deviceInfo.UserAgent))
        {
            deviceInfo.UserAgent = Request.Headers["User-Agent"].ToString();
        }

        var evaluation = await _deviceTrustService.EvaluateDeviceTrustAsync(userId, deviceInfo);
        return Ok(evaluation);
    }
}
