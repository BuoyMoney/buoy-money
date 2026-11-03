using BuoyAI.Core.DTOs.Auth;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for managing device trust and risk-based authentication
/// </summary>
public interface IDeviceTrustService
{
    /// <summary>
    /// Evaluate whether a device should be trusted and if MFA is required
    /// </summary>
    Task<DeviceTrustEvaluationDto> EvaluateDeviceTrustAsync(int userId, DeviceInfoDto deviceInfo);
    
    /// <summary>
    /// Register a new trusted device after successful MFA
    /// </summary>
    Task<int> RegisterTrustedDeviceAsync(int userId, DeviceInfoDto deviceInfo);
    
    /// <summary>
    /// Update the last used timestamp for a trusted device
    /// </summary>
    Task UpdateDeviceLastUsedAsync(int trustedDeviceId);
    
    /// <summary>
    /// Get all trusted devices for a user
    /// </summary>
    Task<List<TrustedDeviceDto>> GetTrustedDevicesAsync(int userId);
    
    /// <summary>
    /// Revoke trust for a specific device
    /// </summary>
    Task RevokeTrustedDeviceAsync(int userId, int trustedDeviceId);
    
    /// <summary>
    /// Revoke all trusted devices for a user
    /// </summary>
    Task RevokeAllTrustedDevicesAsync(int userId);
    
    /// <summary>
    /// Clean up expired trusted devices
    /// </summary>
    Task CleanupExpiredDevicesAsync();
    
    /// <summary>
    /// Log a login attempt for security monitoring
    /// </summary>
    Task LogLoginAttemptAsync(int userId, DeviceInfoDto deviceInfo, bool success, bool mfaChallenged, bool mfaSuccess, int riskScore, string? failureReason = null);
    
    /// <summary>
    /// Calculate risk score for a login attempt
    /// </summary>
    Task<int> CalculateRiskScoreAsync(int userId, DeviceInfoDto deviceInfo);
    
    /// <summary>
    /// Generate device fingerprint from device info
    /// </summary>
    string GenerateDeviceFingerprint(string? userAgent, string? ipAddress);
}
