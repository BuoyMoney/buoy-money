using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;

namespace BuoyAI.Infrastructure.Services;

public class ConsentService : IConsentService
{
    private readonly IUserConsentRepository _userConsentRepository;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<ConsentService> _logger;

    public ConsentService(
        IUserConsentRepository userConsentRepository,
        IUserRepository userRepository,
        ILogger<ConsentService> logger)
    {
        _userConsentRepository = userConsentRepository;
        _userRepository = userRepository;
        _logger = logger;
    }

    public async Task RecordConsentAsync(int userId, RecordConsentRequest request)
    {
        try
        {
            // Validate user exists
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                throw new ArgumentException("User not found");
            }

            // Validate request
            if (string.IsNullOrWhiteSpace(request.ConsentType))
            {
                throw new ArgumentException("ConsentType is required");
            }

            if (string.IsNullOrWhiteSpace(request.ConsentVersion))
            {
                throw new ArgumentException("ConsentVersion is required");
            }

            // Create consent record
            var consent = new UserConsent
            {
                UserId = userId,
                ConsentType = request.ConsentType,
                ConsentVersion = request.ConsentVersion,
                HasConsented = request.HasConsented,
                ConsentedAt = DateTime.UtcNow,
                ConsentText = request.ConsentText,
                IpAddress = request.IpAddress,
                UserAgent = request.UserAgent,
                CreatedAt = DateTime.UtcNow
            };

            await _userConsentRepository.CreateAsync(consent);

            _logger.LogInformation("Recorded consent for user {UserId}: {ConsentType} v{ConsentVersion} = {HasConsented}", 
                userId, request.ConsentType, request.ConsentVersion, request.HasConsented);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording consent for user {UserId}", userId);
            throw;
        }
    }

    public async Task<List<ConsentHistoryResponse>> GetConsentHistoryAsync(int userId)
    {
        try
        {
            var consents = await _userConsentRepository.GetByUserIdAsync(userId);
            
            return consents.Select(c => new ConsentHistoryResponse
            {
                Id = c.Id,
                ConsentType = c.ConsentType,
                ConsentVersion = c.ConsentVersion,
                HasConsented = c.HasConsented,
                ConsentedAt = c.ConsentedAt,
                ConsentText = c.ConsentText
            }).OrderByDescending(c => c.ConsentedAt).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting consent history for user {UserId}", userId);
            throw;
        }
    }

    public async Task<bool> HasValidConsentAsync(int userId, string consentType)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(consentType))
            {
                throw new ArgumentException("ConsentType is required");
            }

            var latestConsent = await _userConsentRepository.GetLatestConsentAsync(userId, consentType);
            return latestConsent?.HasConsented == true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking consent for user {UserId}, type {ConsentType}", userId, consentType);
            throw;
        }
    }

    public async Task RevokeConsentAsync(int userId, string consentType)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(consentType))
            {
                throw new ArgumentException("ConsentType is required");
            }

            // Create a new consent record with HasConsented = false
            var revokeConsent = new UserConsent
            {
                UserId = userId,
                ConsentType = consentType,
                ConsentVersion = "REVOKED",
                HasConsented = false,
                ConsentedAt = DateTime.UtcNow,
                ConsentText = "User revoked consent",
                CreatedAt = DateTime.UtcNow
            };

            await _userConsentRepository.CreateAsync(revokeConsent);

            _logger.LogInformation("Revoked consent for user {UserId}: {ConsentType}", userId, consentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking consent for user {UserId}, type {ConsentType}", userId, consentType);
            throw;
        }
    }

    public async Task<PlaidConsentData> GetPlaidConsentDataAsync(int userId)
    {
        try
        {
            var plaidConsent = await _userConsentRepository.GetLatestConsentAsync(userId, "PLAID_TERMS");
            var plaidPrivacy = await _userConsentRepository.GetLatestConsentAsync(userId, "PLAID_PRIVACY");

            return new PlaidConsentData
            {
                DataSharing = plaidConsent?.HasConsented == true,
                TransactionAccess = plaidConsent?.HasConsented == true,
                AccountAccess = plaidConsent?.HasConsented == true,
                BalanceAccess = plaidConsent?.HasConsented == true,
                IdentityAccess = plaidConsent?.HasConsented == true,
                TermsAccepted = plaidConsent?.HasConsented == true,
                PrivacyPolicyAccepted = plaidPrivacy?.HasConsented == true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Plaid consent data for user {UserId}", userId);
            throw;
        }
    }
}
