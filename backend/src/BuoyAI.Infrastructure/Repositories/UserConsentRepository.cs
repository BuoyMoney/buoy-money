using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class UserConsentRepository : IUserConsentRepository
{
    private readonly BuoyAIDbContext _context;

    public UserConsentRepository(BuoyAIDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<UserConsent>> GetByUserIdAsync(int userId)
    {
        return await _context.UserConsents
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.ConsentedAt)
            .ToListAsync();
    }

    public async Task<UserConsent?> GetByIdAsync(int id)
    {
        return await _context.UserConsents
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<UserConsent?> GetLatestConsentAsync(int userId, string consentType)
    {
        return await _context.UserConsents
            .Where(c => c.UserId == userId && c.ConsentType == consentType)
            .OrderByDescending(c => c.ConsentedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<UserConsent> CreateAsync(UserConsent consent)
    {
        _context.UserConsents.Add(consent);
        await _context.SaveChangesAsync();
        return consent;
    }

    public async Task<IEnumerable<UserConsent>> GetByConsentTypeAsync(string consentType)
    {
        return await _context.UserConsents
            .Where(c => c.ConsentType == consentType)
            .OrderByDescending(c => c.ConsentedAt)
            .ToListAsync();
    }
}
