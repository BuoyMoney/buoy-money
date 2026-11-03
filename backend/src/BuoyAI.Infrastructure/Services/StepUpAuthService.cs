using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OtpNet;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for managing step-up authentication (Tier 2 & 3 MFA)
/// </summary>
public class StepUpAuthService : IStepUpAuthService
{
    private readonly BuoyAIDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ITwoFactorService _twoFactorService;
    private readonly ILogger<StepUpAuthService> _logger;
    private readonly IEncryptionService _encryption;

    // Tier 2 operations require MFA within this session window (30 minutes)
    private const int Tier2SessionMinutes = 30;
    
    // Tier 3 operations require fresh MFA within this time window (5 minutes)
    private const int Tier3FreshMfaMinutes = 5;

    public StepUpAuthService(
        BuoyAIDbContext context,
        IConfiguration configuration,
        ITwoFactorService twoFactorService,
        ILogger<StepUpAuthService> logger,
        IEncryptionService encryption)
    {
        _encryption = encryption;
        _context = context;
        _configuration = configuration;
        _twoFactorService = twoFactorService;
        _logger = logger;
    }

    public async Task<bool> RequiresMfaForOperationAsync(int userId, ProtectedOperation operation)
    {
        try
        {
            var requirementLevel = GetRequirementLevel(operation);

            // Check if user has MFA enabled
            var user = await _context.Users.FindAsync(userId);
            if (user == null || !user.HasMfaEnabled)
            {
                // If MFA not enabled, these operations should still be blocked
                // This is a policy decision - you might want to require MFA setup first
                _logger.LogWarning(
                    "User {UserId} attempted protected operation {Operation} without MFA enabled",
                    userId, operation);
                return true; // Require MFA setup
            }

            switch (requirementLevel)
            {
                case MfaRequirementLevel.None:
                    return false;

                case MfaRequirementLevel.Login:
                    // Handled by device trust service
                    return false;

                case MfaRequirementLevel.Financial:
                    // Tier 2: Check if MFA session is active (within 30 minutes)
                    // This prevents spamming users with MFA requests during batch operations
                    var hasActiveSession = await HasRecentMfaSessionAsync(userId, Tier2SessionMinutes);
                    return !hasActiveSession; // Require MFA if no active session

                case MfaRequirementLevel.Administrative:
                    // Tier 3: Check if recent MFA session exists (within 5 minutes)
                    var hasRecentSession = await HasRecentMfaSessionAsync(userId, Tier3FreshMfaMinutes);
                    return !hasRecentSession; // Require MFA if no recent session

                default:
                    return true; // Fail secure
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Error checking MFA requirement for user {UserId} operation {Operation}", 
                userId, operation);
            return true; // Fail secure
        }
    }

    public Task<StepUpMfaResponse> GenerateOperationTokenAsync(
        int userId, 
        ProtectedOperation operation, 
        int validityMinutes = 5)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.ASCII.GetBytes(
                _configuration["Jwt:Key"] ?? 
                throw new InvalidOperationException("JWT key not configured"));

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim("nameid", userId.ToString()),
                    new Claim("type", "operation_token"),
                    new Claim("operation", operation.ToString())
                }),
                Expires = DateTime.UtcNow.AddMinutes(validityMinutes),
                Issuer = _configuration["Jwt:Issuer"],
                Audience = _configuration["Jwt:Audience"],
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(key),
                    SecurityAlgorithms.HmacSha256Signature)
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            var tokenString = tokenHandler.WriteToken(token);

            _logger.LogInformation(
                "Generated operation token for user {UserId} operation {Operation} valid for {Minutes} minutes",
                userId, operation, validityMinutes);

            return Task.FromResult(new StepUpMfaResponse
            {
                OperationToken = tokenString,
                ExpiresAt = DateTime.UtcNow.AddMinutes(validityMinutes),
                Operation = operation.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Error generating operation token for user {UserId} operation {Operation}",
                userId, operation);
            throw;
        }
    }

    public Task<bool> ValidateOperationTokenAsync(
        string operationToken, 
        ProtectedOperation operation, 
        int userId)
    {
        try
        {
            // Keep JWT claim names verbatim. By default JwtSecurityTokenHandler remaps
            // inbound short claim names (e.g. "nameid") to long ClaimTypes URIs, so the
            // FindFirst("nameid") lookups below would miss and every valid operation
            // token would be rejected as "Invalid operation token structure".
            var tokenHandler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var key = Encoding.ASCII.GetBytes(
                _configuration["Jwt:Key"] ??
                throw new InvalidOperationException("JWT key not configured"));

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = _configuration["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = _configuration["Jwt:Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };

            var principal = tokenHandler.ValidateToken(operationToken, validationParameters, out _);

            // Tolerate either the raw "nameid" claim or the mapped NameIdentifier URI.
            var userIdClaim = principal.FindFirst("nameid")?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var typeClaim = principal.FindFirst("type")?.Value;
            var operationClaim = principal.FindFirst("operation")?.Value;

            if (string.IsNullOrEmpty(userIdClaim) || 
                typeClaim != "operation_token" ||
                string.IsNullOrEmpty(operationClaim))
            {
                _logger.LogWarning("Invalid operation token structure");
                return Task.FromResult(false);
            }

            if (int.Parse(userIdClaim) != userId)
            {
                _logger.LogWarning(
                    "Operation token user ID mismatch. Token: {TokenUserId}, Expected: {ExpectedUserId}",
                    userIdClaim, userId);
                return Task.FromResult(false);
            }

            if (operationClaim != operation.ToString())
            {
                _logger.LogWarning(
                    "Operation token operation mismatch. Token: {TokenOperation}, Expected: {ExpectedOperation}",
                    operationClaim, operation);
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating operation token");
            return Task.FromResult(false);
        }
    }

    public async Task SendStepUpMfaCodeAsync(
        int userId, 
        ProtectedOperation operation, 
        string? ipAddress = null, 
        string? userAgent = null)
    {
        try
        {
            var purpose = $"step_up_{operation.ToString().ToLower()}";
            
            await _twoFactorService.GenerateAndSendCodeAsync(
                userId, 
                purpose, 
                ipAddress, 
                userAgent);

            _logger.LogInformation(
                "Sent step-up MFA code to user {UserId} for operation {Operation}",
                userId, operation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Error sending step-up MFA code for user {UserId} operation {Operation}",
                userId, operation);
            throw;
        }
    }

    public async Task<StepUpMfaResponse?> VerifyStepUpMfaAsync(
        int userId,
        string mfaCode,
        ProtectedOperation operation)
    {
        try
        {
            var purpose = $"step_up_{operation.ToString().ToLower()}";
            var isValid = false;

            // 1. Try email codes from database
            isValid = await _twoFactorService.VerifyCodeAsync(userId, mfaCode, purpose);

            // 2. Try TOTP if email code didn't match
            if (!isValid)
            {
                var user = await _context.Users.FindAsync(userId);
                if (user != null && user.MfaType == MfaType.Totp && !string.IsNullOrEmpty(user.TotpSecret))
                {
                    try
                    {
                        var secretBytes = Base32Encoding.ToBytes(MfaSecretProtector.Reveal(_encryption, user.TotpSecret));
                        var totp = new Totp(secretBytes);
                        var window = new VerificationWindow(previous: 1, future: 1);
                        isValid = totp.VerifyTotp(mfaCode, out _, window);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "TOTP verification error for step-up MFA, user {UserId}", userId);
                    }
                }
            }

            // 3. Try recovery codes as last resort
            if (!isValid)
            {
                var user2 = await _context.Users.FindAsync(userId);
                if (user2 != null && !string.IsNullOrEmpty(user2.RecoveryCodes))
                {
                    List<string> codes;
                    try
                    {
                        codes = MfaSecretProtector.Reveal(_encryption, user2.RecoveryCodes).Split(';').ToList();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Recovery codes unreadable for step-up MFA, user {UserId}", userId);
                        codes = new List<string>();
                    }
                    if (codes.Contains(mfaCode))
                    {
                        isValid = true;
                        codes.Remove(mfaCode);
                        user2.RecoveryCodes = codes.Count > 0 ? MfaSecretProtector.Protect(_encryption, string.Join(";", codes)) : null;
                        user2.UpdatedAt = DateTime.UtcNow;
                        _context.Users.Update(user2);
                        await _context.SaveChangesAsync();
                        _logger.LogInformation("Recovery code used for step-up MFA, user {UserId}, {Remaining} codes remaining", userId, codes.Count);
                    }
                }
            }

            if (!isValid)
            {
                _logger.LogWarning(
                    "Invalid step-up MFA code for user {UserId} operation {Operation}",
                    userId, operation);
                return null;
            }

            // Determine validity period based on tier
            var requirementLevel = GetRequirementLevel(operation);
            var validityMinutes = requirementLevel == MfaRequirementLevel.Administrative 
                ? Tier3FreshMfaMinutes 
                : Tier2SessionMinutes; // Tier 2 uses 30-minute session window

            var operationToken = await GenerateOperationTokenAsync(
                userId, 
                operation, 
                validityMinutes);

            _logger.LogInformation(
                "Successfully verified step-up MFA for user {UserId} operation {Operation}",
                userId, operation);

            return operationToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Error verifying step-up MFA for user {UserId} operation {Operation}",
                userId, operation);
            return null;
        }
    }

    public MfaRequirementLevel GetRequirementLevel(ProtectedOperation operation)
    {
        var operationValue = (int)operation;

        return operationValue switch
        {
            >= 300 => MfaRequirementLevel.Administrative, // Tier 3
            >= 200 => MfaRequirementLevel.Financial,      // Tier 2
            _ => MfaRequirementLevel.None
        };
    }

    public async Task<StepUpMfaResponse?> VerifyStepUpPasskeyAsync(
        int userId,
        string assertionResponse,
        ProtectedOperation operation)
    {
        try
        {
            var dto = new Core.DTOs.PasskeyAuthenticationCompleteDto
            {
                AssertionResponse = assertionResponse
            };
            var user = await _twoFactorService.CompletePasskeyAuthenticationAsync(dto);

            if (user.Id != userId)
            {
                _logger.LogWarning(
                    "Passkey step-up user mismatch: token userId={TokenUserId}, passkey userId={PasskeyUserId}",
                    userId, user.Id);
                return null;
            }

            var requirementLevel = GetRequirementLevel(operation);
            var validityMinutes = requirementLevel == MfaRequirementLevel.Administrative
                ? Tier3FreshMfaMinutes
                : Tier2SessionMinutes;

            var operationToken = await GenerateOperationTokenAsync(userId, operation, validityMinutes);

            _logger.LogInformation(
                "Successfully verified step-up passkey MFA for user {UserId} operation {Operation}",
                userId, operation);

            return operationToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error verifying step-up passkey MFA for user {UserId} operation {Operation}",
                userId, operation);
            return null;
        }
    }

    public async Task<bool> HasRecentMfaSessionAsync(int userId, int withinMinutes = 5)
    {
        try
        {
            var cutoffTime = DateTime.UtcNow.AddMinutes(-withinMinutes);

            var recentMfaCode = await _context.TwoFactorCodes
                .Where(c => c.UserId == userId &&
                           c.IsUsed &&
                           c.UsedAt.HasValue &&
                           c.UsedAt.Value >= cutoffTime &&
                           (c.Purpose.StartsWith("step_up_") || c.Purpose == "login_verification"))
                .OrderByDescending(c => c.UsedAt)
                .FirstOrDefaultAsync();

            return recentMfaCode != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking recent MFA session for user {UserId}", userId);
            return false;
        }
    }
}
