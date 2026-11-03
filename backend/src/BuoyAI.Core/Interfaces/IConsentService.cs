using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces;

public interface IConsentService
{
    Task RecordConsentAsync(int userId, RecordConsentRequest request);
    Task<List<ConsentHistoryResponse>> GetConsentHistoryAsync(int userId);
    Task<bool> HasValidConsentAsync(int userId, string consentType);
    Task RevokeConsentAsync(int userId, string consentType);
    Task<PlaidConsentData> GetPlaidConsentDataAsync(int userId);
}
