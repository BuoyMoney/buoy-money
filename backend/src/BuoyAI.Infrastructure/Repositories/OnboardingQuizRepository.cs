using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class OnboardingQuizRepository : IOnboardingQuizRepository
{
    private readonly BuoyAIDbContext _context;

    public OnboardingQuizRepository(BuoyAIDbContext context)
    {
        _context = context;
    }

    public async Task<UserOnboardingQuiz?> GetByUserIdAsync(int userId)
    {
        return await _context.UserOnboardingQuizzes
            .Include(q => q.User)
            .FirstOrDefaultAsync(q => q.UserId == userId);
    }

    public async Task<UserOnboardingQuiz?> GetLatestByUserIdAsync(int userId)
    {
        return await _context.UserOnboardingQuizzes
            .Include(q => q.User)
            .Where(q => q.UserId == userId)
            .OrderByDescending(q => q.Version)
            .ThenByDescending(q => q.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<UserOnboardingQuiz> CreateAsync(UserOnboardingQuiz quiz)
    {
        quiz.CreatedAt = DateTime.UtcNow;
        quiz.UpdatedAt = DateTime.UtcNow;
        
        // Get the next version number for this user
        var latestVersion = await _context.UserOnboardingQuizzes
            .Where(q => q.UserId == quiz.UserId)
            .MaxAsync(q => (int?)q.Version) ?? 0;
        
        quiz.Version = latestVersion + 1;
        
        _context.UserOnboardingQuizzes.Add(quiz);
        await _context.SaveChangesAsync();
        return quiz;
    }

    public async Task<UserOnboardingQuiz> UpdateAsync(UserOnboardingQuiz quiz)
    {
        quiz.UpdatedAt = DateTime.UtcNow;
        
        _context.UserOnboardingQuizzes.Update(quiz);
        await _context.SaveChangesAsync();
        return quiz;
    }

    public async Task<IEnumerable<UserOnboardingQuiz>> GetVersionHistoryByUserIdAsync(int userId)
    {
        return await _context.UserOnboardingQuizzes
            .Where(q => q.UserId == userId)
            .OrderByDescending(q => q.Version)
            .ThenByDescending(q => q.CreatedAt)
            .ToListAsync();
    }

    public async Task<bool> HasCompletedQuizAsync(int userId)
    {
        return await _context.UserOnboardingQuizzes
            .AnyAsync(q => q.UserId == userId);
    }
}
