using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for managing step-up authentication (Tier 2 & 3 MFA)
/// </summary>
public interface IStepUpAuthService
{
    Task<bool> RequiresMfaForOperationAsync(int userId, ProtectedOperation operation);
    Task<StepUpMfaResponse> GenerateOperationTokenAsync(int userId, ProtectedOperation operation, int validityMinutes = 5);
    Task<bool> ValidateOperationTokenAsync(string operationToken, ProtectedOperation operation, int userId);
    Task SendStepUpMfaCodeAsync(int userId, ProtectedOperation operation, string? ipAddress = null, string? userAgent = null);
    Task<StepUpMfaResponse?> VerifyStepUpMfaAsync(int userId, string mfaCode, ProtectedOperation operation);
    MfaRequirementLevel GetRequirementLevel(ProtectedOperation operation);
    Task<bool> HasRecentMfaSessionAsync(int userId, int withinMinutes = 5);
    Task<StepUpMfaResponse?> VerifyStepUpPasskeyAsync(int userId, string assertionResponse, ProtectedOperation operation);
}
