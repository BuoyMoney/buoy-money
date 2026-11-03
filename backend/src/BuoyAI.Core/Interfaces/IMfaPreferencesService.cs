using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for managing user MFA preferences
/// </summary>
public interface IMfaPreferencesService
{
    /// <summary>
    /// Get user's MFA preferences
    /// </summary>
    Task<UserMfaPreferencesDto> GetPreferencesAsync(int userId);
    
    /// <summary>
    /// Update user's MFA preferences
    /// </summary>
    Task UpdatePreferencesAsync(int userId, UpdateMfaPreferencesDto preferences);
    
    /// <summary>
    /// Get the user's preferred MFA method
    /// </summary>
    Task<MfaType> GetPreferredMfaMethodAsync(int userId);
    
    /// <summary>
    /// Get available MFA methods for a user
    /// </summary>
    Task<List<string>> GetAvailableMethodsAsync(int userId);
}
