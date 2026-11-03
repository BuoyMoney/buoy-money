using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class DeviceController : BaseController
{
    private readonly IDeviceTrustService _deviceTrustService;
    private readonly ILogger<DeviceController> _logger;

    public DeviceController(
        IDeviceTrustService deviceTrustService,
        ILogger<DeviceController> logger)
    {
        _deviceTrustService = deviceTrustService;
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
        return Ok(new { message = "Device trust revoked successfully" });
    }

    /// <summary>
    /// Revoke all trusted devices
    /// </summary>
    [HttpPost("revoke-all")]
    public async Task<IActionResult> RevokeAllTrustedDevices()
    {
        var userId = GetCurrentUserId();
        await _deviceTrustService.RevokeAllTrustedDevicesAsync(userId);
        return Ok(new { message = "All devices revoked successfully" });
    }
}
