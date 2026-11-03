using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for managing device trust and risk-based authentication
/// </summary>
public class DeviceTrustService : IDeviceTrustService
{
    private readonly BuoyAIDbContext _context;
    private readonly ILogger<DeviceTrustService> _logger;
    private readonly IIpGeolocationService _geolocationService;
    private const int TrustPeriodDays = 30;
    private const int HighRiskThreshold = 70;
    private const int MediumRiskThreshold = 40;

    public DeviceTrustService(
        BuoyAIDbContext context,
        ILogger<DeviceTrustService> logger,
        IIpGeolocationService geolocationService)
    {
        _context = context;
        _logger = logger;
        _geolocationService = geolocationService;
    }

    public async Task<DeviceTrustEvaluationDto> EvaluateDeviceTrustAsync(int userId, DeviceInfoDto deviceInfo)
    {
        try
        {
            var fingerprint = GenerateDeviceFingerprint(deviceInfo.UserAgent, deviceInfo.IpAddress);
            var riskScore = await CalculateRiskScoreAsync(userId, deviceInfo);
            
            // Check if device is already trusted
            var trustedDevice = await _context.TrustedDevices
                .Where(td => td.UserId == userId && 
                            td.DeviceFingerprint == fingerprint && 
                            td.IsActive && 
                            td.ExpiresAt > DateTime.UtcNow)
                .FirstOrDefaultAsync();

            var evaluation = new DeviceTrustEvaluationDto
            {
                IsTrustedDevice = trustedDevice != null,
                TrustedDeviceId = trustedDevice?.Id,
                RiskScore = riskScore
            };

            // Determine if MFA is required
            var mfaReasons = new List<string>();

            if (trustedDevice == null)
            {
                mfaReasons.Add("New or untrusted device");
                evaluation.RequiresMfa = true;
            }

            if (riskScore >= HighRiskThreshold)
            {
                mfaReasons.Add($"High risk activity detected (score: {riskScore})");
                evaluation.RequiresMfa = true;
            }
            else if (riskScore >= MediumRiskThreshold && trustedDevice == null)
            {
                mfaReasons.Add($"Medium risk activity from new device (score: {riskScore})");
                evaluation.RequiresMfa = true;
            }

            evaluation.MfaReasons = mfaReasons;

            _logger.LogInformation(
                "Device trust evaluation for user {UserId}: Trusted={IsTrusted}, RequiresMFA={RequiresMfa}, RiskScore={RiskScore}",
                userId, evaluation.IsTrustedDevice, evaluation.RequiresMfa, evaluation.RiskScore);

            return evaluation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating device trust for user {UserId}", userId);
            // Fail secure: require MFA on error
            return new DeviceTrustEvaluationDto
            {
                IsTrustedDevice = false,
                RequiresMfa = true,
                RiskScore = 100,
                MfaReasons = new List<string> { "Unable to verify device trust" }
            };
        }
    }

    public async Task<int> RegisterTrustedDeviceAsync(int userId, DeviceInfoDto deviceInfo)
    {
        try
        {
            var fingerprint = GenerateDeviceFingerprint(deviceInfo.UserAgent, deviceInfo.IpAddress);
            
            // Check if device already exists
            var existingDevice = await _context.TrustedDevices
                .Where(td => td.UserId == userId && td.DeviceFingerprint == fingerprint)
                .FirstOrDefaultAsync();

            if (existingDevice != null)
            {
                // Update existing device
                existingDevice.LastUsedAt = DateTime.UtcNow;
                existingDevice.ExpiresAt = DateTime.UtcNow.AddDays(TrustPeriodDays);
                existingDevice.IsActive = true;
                existingDevice.UpdatedAt = DateTime.UtcNow;
                existingDevice.IpAddress = deviceInfo.IpAddress;
                
                await _context.SaveChangesAsync();
                
                _logger.LogInformation(
                    "Updated existing trusted device {DeviceId} for user {UserId}",
                    existingDevice.Id, userId);
                
                return existingDevice.Id;
            }

            // Create new trusted device
            var deviceType = DetermineDeviceType(deviceInfo.UserAgent);
            var deviceName = GenerateDeviceName(deviceType, deviceInfo.UserAgent);
            
            var trustedDevice = new TrustedDevice
            {
                UserId = userId,
                DeviceFingerprint = fingerprint,
                DeviceName = deviceName,
                DeviceType = deviceType,
                IpAddress = deviceInfo.IpAddress,
                UserAgent = deviceInfo.UserAgent,
                Location = null, // Location is populated from geolocation service in LoginAttempts
                LastUsedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(TrustPeriodDays),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.TrustedDevices.Add(trustedDevice);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Registered new trusted device {DeviceId} for user {UserId}: {DeviceName}",
                trustedDevice.Id, userId, deviceName);

            return trustedDevice.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering trusted device for user {UserId}", userId);
            throw;
        }
    }

    public async Task UpdateDeviceLastUsedAsync(int trustedDeviceId)
    {
        try
        {
            var device = await _context.TrustedDevices.FindAsync(trustedDeviceId);
            if (device != null)
            {
                device.LastUsedAt = DateTime.UtcNow;
                device.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating last used for trusted device {DeviceId}", trustedDeviceId);
            // Don't throw - this is not critical
        }
    }

    public async Task<List<TrustedDeviceDto>> GetTrustedDevicesAsync(int userId)
    {
        try
        {
            var now = DateTime.UtcNow;
            var devices = await _context.TrustedDevices
                .Where(td => td.UserId == userId && td.IsActive)
                .OrderByDescending(td => td.LastUsedAt)
                .ToListAsync();

            // Get the most recent login attempt for each device to get location info
            var deviceDtos = new List<TrustedDeviceDto>();
            foreach (var device in devices)
            {
                var lastLogin = await _context.LoginAttempts
                    .Where(la => la.UserId == userId && 
                                la.DeviceFingerprint == device.DeviceFingerprint &&
                                la.Success)
                    .OrderByDescending(la => la.AttemptedAt)
                    .FirstOrDefaultAsync();

                var daysUntilExpiration = (int)(device.ExpiresAt - now).TotalDays;
                
                deviceDtos.Add(new TrustedDeviceDto
                {
                    Id = device.Id,
                    DeviceFingerprint = device.DeviceFingerprint,
                    DeviceName = device.DeviceName,
                    DeviceType = device.DeviceType,
                    Location = device.Location,
                    LastUsedAt = device.LastUsedAt,
                    ExpiresAt = device.ExpiresAt,
                    IsActive = device.IsActive,
                    CreatedAt = device.CreatedAt,
                    IsCurrentDevice = false, // Will be set by frontend based on current fingerprint
                    DaysUntilExpiration = Math.Max(0, daysUntilExpiration),
                    LastUsedIpAddress = lastLogin?.IpAddress,
                    LastUsedLocation = lastLogin != null && !string.IsNullOrEmpty(lastLogin.City)
                        ? $"{lastLogin.City}, {lastLogin.Region ?? lastLogin.Country}"
                        : lastLogin?.Location
                });
            }

            return deviceDtos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting trusted devices for user {UserId}", userId);
            return new List<TrustedDeviceDto>();
        }
    }

    public async Task RevokeTrustedDeviceAsync(int userId, int trustedDeviceId)
    {
        try
        {
            var device = await _context.TrustedDevices
                .Where(td => td.Id == trustedDeviceId && td.UserId == userId)
                .FirstOrDefaultAsync();

            if (device != null)
            {
                device.IsActive = false;
                device.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                _logger.LogInformation(
                    "Revoked trusted device {DeviceId} for user {UserId}",
                    trustedDeviceId, userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking trusted device {DeviceId} for user {UserId}", trustedDeviceId, userId);
            throw;
        }
    }

    public async Task RevokeAllTrustedDevicesAsync(int userId)
    {
        try
        {
            var devices = await _context.TrustedDevices
                .Where(td => td.UserId == userId && td.IsActive)
                .ToListAsync();

            foreach (var device in devices)
            {
                device.IsActive = false;
                device.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Revoked all {Count} trusted devices for user {UserId}",
                devices.Count, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking all trusted devices for user {UserId}", userId);
            throw;
        }
    }

    public async Task CleanupExpiredDevicesAsync()
    {
        try
        {
            var expiredDevices = await _context.TrustedDevices
                .Where(td => td.IsActive && td.ExpiresAt < DateTime.UtcNow)
                .ToListAsync();

            foreach (var device in expiredDevices)
            {
                device.IsActive = false;
                device.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Cleaned up {Count} expired trusted devices", expiredDevices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up expired devices");
        }
    }

    public async Task LogLoginAttemptAsync(
        int userId, 
        DeviceInfoDto deviceInfo, 
        bool success, 
        bool mfaChallenged, 
        bool mfaSuccess, 
        int riskScore, 
        string? failureReason = null)
    {
        try
        {
            var fingerprint = GenerateDeviceFingerprint(deviceInfo.UserAgent, deviceInfo.IpAddress);
            var isTrusted = await _context.TrustedDevices
                .AnyAsync(td => td.UserId == userId && 
                               td.DeviceFingerprint == fingerprint && 
                               td.IsActive && 
                               td.ExpiresAt > DateTime.UtcNow);

            var now = DateTime.UtcNow;
            var loginAttempt = new LoginAttempt
            {
                UserId = userId,
                IpAddress = deviceInfo.IpAddress,
                UserAgent = deviceInfo.UserAgent,
                Location = null, // Location is populated from geolocation service below
                DeviceFingerprint = fingerprint,
                Success = success,
                MfaChallenged = mfaChallenged,
                MfaSuccess = mfaSuccess,
                TrustedDevice = isTrusted,
                RiskScore = riskScore,
                FailureReason = failureReason,
                AttemptedAt = now,
                HourOfDay = now.Hour,
                DayOfWeek = (int)now.DayOfWeek
            };

            // Enrich with geolocation data if available
            try
            {
                if (!string.IsNullOrEmpty(deviceInfo.IpAddress))
                {
                    var geoLocation = await _geolocationService.GetLocationAsync(deviceInfo.IpAddress);
                    if (geoLocation != null)
                    {
                        loginAttempt.Country = geoLocation.Country;
                        loginAttempt.CountryCode = geoLocation.CountryCode;
                        loginAttempt.Region = geoLocation.Region;
                        loginAttempt.City = geoLocation.City;
                        loginAttempt.Latitude = geoLocation.Latitude;
                        loginAttempt.Longitude = geoLocation.Longitude;
                        loginAttempt.Timezone = geoLocation.Timezone;
                    }
                }
            }
            catch (Exception geoEx)
            {
                _logger.LogWarning(geoEx, "Failed to get geolocation for IP {IpAddress}", deviceInfo.IpAddress);
                // Continue without geolocation data - don't block login
            }

            _context.LoginAttempts.Add(loginAttempt);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Logged login attempt for user {UserId}: Success={Success}, MFA={MfaChallenged}, Risk={RiskScore}, Location={Location}",
                userId, success, mfaChallenged, riskScore, loginAttempt.City ?? "Unknown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging login attempt for user {UserId}", userId);
            // Don't throw - logging failure shouldn't break login flow
        }
    }

    public async Task<int> CalculateRiskScoreAsync(int userId, DeviceInfoDto deviceInfo)
    {
        try
        {
            int riskScore = 0;
            var fingerprint = GenerateDeviceFingerprint(deviceInfo.UserAgent, deviceInfo.IpAddress);

            // Check if this device has been seen before
            var deviceSeen = await _context.TrustedDevices
                .AnyAsync(td => td.UserId == userId && td.DeviceFingerprint == fingerprint);
            
            if (!deviceSeen)
            {
                // Check if ever logged in from device at all (even expired)
                var everSeenDevice = await _context.LoginAttempts
                    .AnyAsync(la => la.UserId == userId && la.DeviceFingerprint == fingerprint);
                
                if (!everSeenDevice)
                {
                    riskScore += 30; // Completely new device
                }
                else
                {
                    riskScore += 15; // Previously seen but not currently trusted
                }
            }

            // Check recent failed login attempts
            var recentFailures = await _context.LoginAttempts
                .Where(la => la.UserId == userId && 
                            !la.Success && 
                            la.AttemptedAt > DateTime.UtcNow.AddHours(-24))
                .CountAsync();

            if (recentFailures >= 5)
            {
                riskScore += 40;
            }
            else if (recentFailures >= 3)
            {
                riskScore += 25;
            }
            else if (recentFailures >= 1)
            {
                riskScore += 10;
            }

            // Check if IP address has been seen before
            var ipSeen = await _context.LoginAttempts
                .AnyAsync(la => la.UserId == userId && 
                               la.IpAddress == deviceInfo.IpAddress && 
                               la.Success);

            if (!ipSeen)
            {
                riskScore += 15; // New IP address
            }

            // ENHANCED: Geographic location analysis
            try
            {
                if (!string.IsNullOrEmpty(deviceInfo.IpAddress))
                {
                    var currentLocation = await _geolocationService.GetLocationAsync(deviceInfo.IpAddress);
                    if (currentLocation != null)
                    {
                        var lastLocation = await GetLastSuccessfulLoginLocationAsync(userId);
                        
                        if (lastLocation != null)
                        {
                            // Check if logging in from different country
                            if (currentLocation.CountryCode != lastLocation.CountryCode)
                            {
                                riskScore += 20; // Different country
                            }
                            
                            // Calculate distance between locations
                            var distance = _geolocationService.CalculateDistance(
                                lastLocation.Latitude,
                                lastLocation.Longitude,
                                currentLocation.Latitude,
                                currentLocation.Longitude);
                            
                            if (distance > 100) // More than 100km away
                            {
                                riskScore += 25;
                            }
                        }
                        
                        // ENHANCED: Velocity check - multiple distinct locations in 24 hours
                        var recentLocations = await GetRecentLoginLocationsAsync(userId, hours: 24);
                        if (recentLocations.Count >= 3)
                        {
                            riskScore += 30; // Impossible travel pattern
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Geolocation check failed for user {UserId}, continuing without location data", userId);
                // Don't fail the entire risk calculation if geolocation fails
            }

            // ENHANCED: Time-of-day pattern analysis
            var currentHour = DateTime.UtcNow.Hour;
            var typicalHours = await GetTypicalLoginHoursAsync(userId);
            
            if (typicalHours.Any() && !typicalHours.Contains(currentHour))
            {
                riskScore += 10; // Unusual login time for this user
            }
            else if (currentHour >= 1 && currentHour <= 5)
            {
                // Late night/early morning logins are slightly more suspicious
                riskScore += 5;
            }

            // Check if logging in from multiple locations quickly
            var recentLogins = await _context.LoginAttempts
                .Where(la => la.UserId == userId && 
                            la.Success && 
                            la.AttemptedAt > DateTime.UtcNow.AddHours(-1))
                .OrderByDescending(la => la.AttemptedAt)
                .Take(2)
                .ToListAsync();

            if (recentLogins.Count >= 2 && 
                !string.IsNullOrEmpty(recentLogins[0].IpAddress) && 
                !string.IsNullOrEmpty(recentLogins[1].IpAddress) &&
                recentLogins[0].IpAddress != recentLogins[1].IpAddress)
            {
                riskScore += 20; // Different IPs within an hour
            }

            // Cap risk score at 100
            return Math.Min(riskScore, 100);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating risk score for user {UserId}", userId);
            return 50; // Return medium risk on error
        }
    }
    
    /// <summary>
    /// Get the location of the last successful login
    /// </summary>
    private async Task<GeoLocationDto?> GetLastSuccessfulLoginLocationAsync(int userId)
    {
        var lastLogin = await _context.LoginAttempts
            .Where(la => la.UserId == userId && 
                        la.Success && 
                        la.Latitude.HasValue && 
                        la.Longitude.HasValue)
            .OrderByDescending(la => la.AttemptedAt)
            .FirstOrDefaultAsync();
        
        if (lastLogin == null || !lastLogin.Latitude.HasValue || !lastLogin.Longitude.HasValue)
            return null;
        
        return new GeoLocationDto
        {
            Country = lastLogin.Country ?? string.Empty,
            CountryCode = lastLogin.CountryCode ?? string.Empty,
            Region = lastLogin.Region ?? string.Empty,
            City = lastLogin.City ?? string.Empty,
            Latitude = lastLogin.Latitude.Value,
            Longitude = lastLogin.Longitude.Value,
            Timezone = lastLogin.Timezone ?? string.Empty,
            Isp = string.Empty
        };
    }
    
    /// <summary>
    /// Get typical login hours for the user (mode of HourOfDay from successful logins)
    /// </summary>
    private async Task<List<int>> GetTypicalLoginHoursAsync(int userId)
    {
        var recentLogins = await _context.LoginAttempts
            .Where(la => la.UserId == userId && 
                        la.Success && 
                        la.HourOfDay.HasValue &&
                        la.AttemptedAt > DateTime.UtcNow.AddDays(-30))
            .Select(la => la.HourOfDay!.Value)
            .ToListAsync();
        
        if (!recentLogins.Any())
            return new List<int>();
        
        // Get hours that appear most frequently (within top 50% of frequency)
        var hourGroups = recentLogins
            .GroupBy(h => h)
            .OrderByDescending(g => g.Count())
            .ToList();
        
        if (!hourGroups.Any())
            return new List<int>();
        
        var maxCount = hourGroups.First().Count();
        var threshold = maxCount * 0.5; // Top 50% frequency
        
        return hourGroups
            .Where(g => g.Count() >= threshold)
            .Select(g => g.Key)
            .ToList();
    }
    
    /// <summary>
    /// Get distinct login locations in the past N hours
    /// </summary>
    private async Task<List<GeoLocationDto>> GetRecentLoginLocationsAsync(int userId, int hours)
    {
        var cutoffTime = DateTime.UtcNow.AddHours(-hours);
        
        var locations = await _context.LoginAttempts
            .Where(la => la.UserId == userId && 
                        la.Success && 
                        la.AttemptedAt > cutoffTime &&
                        la.Latitude.HasValue && 
                        la.Longitude.HasValue)
            .Select(la => new GeoLocationDto
            {
                Country = la.Country ?? string.Empty,
                CountryCode = la.CountryCode ?? string.Empty,
                Region = la.Region ?? string.Empty,
                City = la.City ?? string.Empty,
                Latitude = la.Latitude!.Value,
                Longitude = la.Longitude!.Value,
                Timezone = la.Timezone ?? string.Empty,
                Isp = string.Empty
            })
            .ToListAsync();
        
        // Remove duplicates based on proximity (locations within 10km are considered same)
        var distinctLocations = new List<GeoLocationDto>();
        foreach (var location in locations)
        {
            var isDuplicate = false;
            foreach (var existing in distinctLocations)
            {
                var distance = _geolocationService.CalculateDistance(
                    existing.Latitude,
                    existing.Longitude,
                    location.Latitude,
                    location.Longitude);
                
                if (distance < 10) // Within 10km
                {
                    isDuplicate = true;
                    break;
                }
            }
            
            if (!isDuplicate)
            {
                distinctLocations.Add(location);
            }
        }
        
        return distinctLocations;
    }

    public string GenerateDeviceFingerprint(string? userAgent, string? ipAddress)
    {
        // Create a fingerprint from user agent and IP
        // In production, you might want to include more browser fingerprinting data
        var fingerprintData = $"{userAgent ?? "unknown"}|{ipAddress ?? "unknown"}";
        
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(fingerprintData));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    #region Private Helper Methods

    private string DetermineDeviceType(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
            return "Unknown";

        var ua = userAgent.ToLowerInvariant();

        if (ua.Contains("mobile") || ua.Contains("iphone") || ua.Contains("android") || ua.Contains("ipod"))
            return "Mobile";
        if (ua.Contains("tablet") || ua.Contains("ipad"))
            return "Tablet";
        if (ua.Contains("mac") || ua.Contains("windows") || ua.Contains("linux"))
            return "Desktop";

        return "Unknown";
    }

    private string GenerateDeviceName(string deviceType, string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
            return $"{deviceType} Device";

        var ua = userAgent.ToLowerInvariant();

        // Try to identify specific devices
        if (ua.Contains("iphone"))
            return "iPhone";
        if (ua.Contains("ipad"))
            return "iPad";
        if (ua.Contains("android"))
        {
            if (ua.Contains("mobile"))
                return "Android Phone";
            return "Android Device";
        }
        if (ua.Contains("mac"))
            return "Mac";
        if (ua.Contains("windows"))
            return "Windows PC";
        if (ua.Contains("linux"))
            return "Linux PC";

        return $"{deviceType} Device";
    }

    #endregion
}
