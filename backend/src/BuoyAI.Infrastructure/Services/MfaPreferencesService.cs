using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for managing user MFA preferences
/// </summary>
public class MfaPreferencesService : IMfaPreferencesService
{
    private readonly BuoyAIDbContext _context;
    private readonly ILogger<MfaPreferencesService> _logger;

    public MfaPreferencesService(
        BuoyAIDbContext context,
        ILogger<MfaPreferencesService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<UserMfaPreferencesDto> GetPreferencesAsync(int userId)
    {
        try
        {
            var preferences = await _context.UserMfaPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId);

            // Get available methods for this user
            var availableMethods = await GetAvailableMethodsAsync(userId);

            if (preferences == null)
            {
                // Create default preferences
                var defaultMethod = availableMethods.Contains("Passkey") ? MfaType.Passkey :
                                   availableMethods.Contains("Totp") ? MfaType.Totp :
                                   MfaType.Email;

                preferences = new UserMfaPreferences
                {
                    UserId = userId,
                    PrimaryMethod = defaultMethod,
                    NotifyOnNewDevice = true,
                    NotifyOnSuspiciousLogin = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                
                _context.UserMfaPreferences.Add(preferences);
                await _context.SaveChangesAsync();
            }

            return new UserMfaPreferencesDto
            {
                PrimaryMethod = preferences.PrimaryMethod.ToString(),
                FallbackMethod = preferences.FallbackMethod?.ToString(),
                NotifyOnNewDevice = preferences.NotifyOnNewDevice,
                NotifyOnSuspiciousLogin = preferences.NotifyOnSuspiciousLogin,
                AvailableMethods = availableMethods
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting MFA preferences for user {UserId}", userId);
            throw;
        }
    }

    public async Task UpdatePreferencesAsync(int userId, UpdateMfaPreferencesDto dto)
    {
        try
        {
            var preferences = await _context.UserMfaPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId);

            if (preferences == null)
            {
                preferences = new UserMfaPreferences
                {
                    UserId = userId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.UserMfaPreferences.Add(preferences);
            }

            // Validate that the user has the requested MFA method available
            var availableMethods = await GetAvailableMethodsAsync(userId);
            
            if (!availableMethods.Contains(dto.PrimaryMethod))
            {
                throw new InvalidOperationException($"MFA method {dto.PrimaryMethod} is not available for this user");
            }

            if (dto.FallbackMethod != null && !availableMethods.Contains(dto.FallbackMethod))
            {
                throw new InvalidOperationException($"Fallback MFA method {dto.FallbackMethod} is not available for this user");
            }

            // Parse and update
            preferences.PrimaryMethod = Enum.Parse<MfaType>(dto.PrimaryMethod);
            preferences.FallbackMethod = dto.FallbackMethod != null ? Enum.Parse<MfaType>(dto.FallbackMethod) : null;
            preferences.NotifyOnNewDevice = dto.NotifyOnNewDevice;
            preferences.NotifyOnSuspiciousLogin = dto.NotifyOnSuspiciousLogin;
            preferences.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Updated MFA preferences for user {UserId}: Primary={Primary}, Fallback={Fallback}",
                userId, preferences.PrimaryMethod, preferences.FallbackMethod);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating MFA preferences for user {UserId}", userId);
            throw;
        }
    }

    public async Task<MfaType> GetPreferredMfaMethodAsync(int userId)
    {
        try
        {
            var preferences = await _context.UserMfaPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId);

            if (preferences != null)
            {
                return preferences.PrimaryMethod;
            }

            // No preferences set - determine from what user has configured
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return MfaType.Email;
            }

            // Check for passkeys
            var hasPasskeys = await _context.UserPasskeys
                .AnyAsync(p => p.UserId == userId);
            
            if (hasPasskeys)
            {
                return MfaType.Passkey;
            }

            // Check for TOTP
            if (!string.IsNullOrEmpty(user.TotpSecret))
            {
                return MfaType.Totp;
            }

            // Default to email
            return MfaType.Email;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting preferred MFA method for user {UserId}", userId);
            return MfaType.Email; // Safe default
        }
    }

    public async Task<List<string>> GetAvailableMethodsAsync(int userId)
    {
        try
        {
            var availableMethods = new List<string> { "Email" }; // Email is always available

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return availableMethods;
            }

            // Check for TOTP
            if (!string.IsNullOrEmpty(user.TotpSecret))
            {
                availableMethods.Add("Totp");
            }

            // Check for Passkeys
            var hasPasskeys = await _context.UserPasskeys
                .AnyAsync(p => p.UserId == userId);
            
            if (hasPasskeys)
            {
                availableMethods.Add("Passkey");
            }

            return availableMethods;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available MFA methods for user {UserId}", userId);
            return new List<string> { "Email" }; // Safe default
        }
    }
}
