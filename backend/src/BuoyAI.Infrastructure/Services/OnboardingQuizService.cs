using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BuoyAI.Infrastructure.Services;

public class OnboardingQuizService : IOnboardingQuizService
{
    private readonly IOnboardingQuizRepository _quizRepository;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<OnboardingQuizService> _logger;

    public OnboardingQuizService(
        IOnboardingQuizRepository quizRepository,
        IUserRepository userRepository,
        ILogger<OnboardingQuizService> logger)
    {
        _quizRepository = quizRepository;
        _userRepository = userRepository;
        _logger = logger;
    }

    public async Task<OnboardingQuizResponseDto> SubmitQuizAsync(int userId, OnboardingQuizSubmissionDto submission)
    {
        try
        {
            // Verify user exists
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                throw new ArgumentException($"User with ID {userId} not found");
            }

            // Create new quiz entity
            var quiz = new UserOnboardingQuiz
            {
                UserId = userId,
                PrimaryFinancialGoal = submission.PrimaryFinancialGoal,
                BiggestChallenge = submission.BiggestChallenge,
                SpendingPersonality = submission.SpendingPersonality,
                SubscriptionAwareness = submission.SubscriptionAwareness,
                SavingsBarriers = submission.SavingsBarriers,
                PreferredInsightType = submission.PreferredInsightType,
                FinancialExperience = submission.FinancialExperience,
                HasUsedBudgetingApps = submission.HasUsedBudgetingApps,
                AdditionalNotes = submission.AdditionalNotes
            };

            // Save to database
            var createdQuiz = await _quizRepository.CreateAsync(quiz);
            
            _logger.LogInformation("User {UserId} completed onboarding quiz version {Version}", 
                userId, createdQuiz.Version);

            return MapToDto(createdQuiz);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting onboarding quiz for user {UserId}", userId);
            throw;
        }
    }

    public async Task<OnboardingQuizResponseDto?> GetLatestQuizAsync(int userId)
    {
        try
        {
            var quiz = await _quizRepository.GetLatestByUserIdAsync(userId);
            return quiz != null ? MapToDto(quiz) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving latest quiz for user {UserId}", userId);
            throw;
        }
    }

    public async Task<IEnumerable<OnboardingQuizResponseDto>> GetQuizHistoryAsync(int userId)
    {
        try
        {
            var quizzes = await _quizRepository.GetVersionHistoryByUserIdAsync(userId);
            return quizzes.Select(MapToDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving quiz history for user {UserId}", userId);
            throw;
        }
    }

    public async Task<bool> HasCompletedQuizAsync(int userId)
    {
        try
        {
            return await _quizRepository.HasCompletedQuizAsync(userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking quiz completion for user {UserId}", userId);
            throw;
        }
    }

    public async Task<IEnumerable<PersonalizedInsightDto>> GeneratePersonalizedInsightsAsync(int userId)
    {
        try
        {
            var quiz = await _quizRepository.GetLatestByUserIdAsync(userId);
            if (quiz == null)
            {
                return Enumerable.Empty<PersonalizedInsightDto>();
            }

            var insights = new List<PersonalizedInsightDto>();

            // Generate insights based on quiz responses
            insights.AddRange(GenerateGoalBasedInsights(quiz));
            insights.AddRange(GenerateChallengeBasedInsights(quiz));
            insights.AddRange(GenerateSpendingPersonalityInsights(quiz));
            insights.AddRange(GenerateSubscriptionInsights(quiz));
            insights.AddRange(GenerateSavingsBarrierInsights(quiz));

            return insights;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating personalized insights for user {UserId}", userId);
            throw;
        }
    }

    public async Task<UserProfileForAIDto?> GetUserProfileForAIAsync(int userId)
    {
        try
        {
            var quiz = await _quizRepository.GetLatestByUserIdAsync(userId);
            if (quiz == null)
            {
                return null;
            }

            return new UserProfileForAIDto
            {
                UserId = quiz.UserId,
                PrimaryFinancialGoals = ParseJsonArray(quiz.PrimaryFinancialGoal),
                BiggestChallenges = ParseJsonArray(quiz.BiggestChallenge),
                SpendingPersonalities = ParseJsonArray(quiz.SpendingPersonality),
                SubscriptionAwareness = quiz.SubscriptionAwareness ?? string.Empty,
                SavingsBarriers = ParseJsonArray(quiz.SavingsBarriers),
                PreferredInsightTypes = ParseJsonArray(quiz.PreferredInsightType),
                FinancialExperience = quiz.FinancialExperience ?? string.Empty,
                HasUsedBudgetingApps = quiz.HasUsedBudgetingApps,
                AdditionalNotes = quiz.AdditionalNotes,
                QuizCompletedAt = quiz.CreatedAt,
                QuizVersion = quiz.Version
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving user profile for AI for user {UserId}", userId);
            throw;
        }
    }

    private static string[] ParseJsonArray(string? jsonString)
    {
        if (string.IsNullOrEmpty(jsonString))
        {
            return Array.Empty<string>();
        }

        try
        {
            // Handle both JSON array format ["item1", "item2"] and single values "item1"
            if (jsonString.StartsWith('[') && jsonString.EndsWith(']'))
            {
                var array = JsonSerializer.Deserialize<string[]>(jsonString);
                return array ?? Array.Empty<string>();
            }
            else
            {
                // Single value, return as array with one item
                return new[] { jsonString };
            }
        }
        catch (JsonException)
        {
            // If JSON parsing fails, return single item array
            return new[] { jsonString };
        }
    }

    private static OnboardingQuizResponseDto MapToDto(UserOnboardingQuiz quiz)
    {
        return new OnboardingQuizResponseDto
        {
            Id = quiz.Id,
            UserId = quiz.UserId,
            PrimaryFinancialGoal = quiz.PrimaryFinancialGoal,
            BiggestChallenge = quiz.BiggestChallenge,
            SpendingPersonality = quiz.SpendingPersonality,
            SubscriptionAwareness = quiz.SubscriptionAwareness,
            SavingsBarriers = quiz.SavingsBarriers,
            PreferredInsightType = quiz.PreferredInsightType,
            FinancialExperience = quiz.FinancialExperience,
            HasUsedBudgetingApps = quiz.HasUsedBudgetingApps,
            AdditionalNotes = quiz.AdditionalNotes,
            CreatedAt = quiz.CreatedAt,
            UpdatedAt = quiz.UpdatedAt,
            Version = quiz.Version
        };
    }

    private static IEnumerable<PersonalizedInsightDto> GenerateGoalBasedInsights(UserOnboardingQuiz quiz)
    {
        var insights = new List<PersonalizedInsightDto>();

        switch (quiz.PrimaryFinancialGoal)
        {
            case "save_more":
                insights.Add(new PersonalizedInsightDto
                {
                    InsightType = "savings_strategy",
                    Title = "Boost Your Savings Strategy",
                    Message = "Based on your goal to save more, we'll focus on finding opportunities to automate your savings and optimize your spending.",
                    RecommendedActions = new[] { "Set up automatic transfers", "Review discretionary spending", "Explore high-yield savings accounts" },
                    Priority = "high"
                });
                break;
            case "budget_better":
                insights.Add(new PersonalizedInsightDto
                {
                    InsightType = "budgeting_tips",
                    Title = "Master Your Budget",
                    Message = "We'll help you create and stick to a realistic budget that works with your lifestyle.",
                    RecommendedActions = new[] { "Use the 50/30/20 rule", "Track spending weekly", "Set up budget alerts" },
                    Priority = "high"
                });
                break;
            case "reduce_debt":
                insights.Add(new PersonalizedInsightDto
                {
                    InsightType = "debt_management",
                    Title = "Debt Reduction Plan",
                    Message = "Let's create a strategic plan to tackle your debt efficiently.",
                    RecommendedActions = new[] { "List all debts by interest rate", "Consider debt avalanche method", "Look for extra payment opportunities" },
                    Priority = "high"
                });
                break;
        }

        return insights;
    }

    private static IEnumerable<PersonalizedInsightDto> GenerateChallengeBasedInsights(UserOnboardingQuiz quiz)
    {
        var insights = new List<PersonalizedInsightDto>();

        switch (quiz.BiggestChallenge)
        {
            case "sticking_to_budget":
                insights.Add(new PersonalizedInsightDto
                {
                    InsightType = "budget_adherence",
                    Title = "Budget Sticking Strategies",
                    Message = "We'll send you gentle reminders and positive reinforcement to help you stay on track.",
                    RecommendedActions = new[] { "Set realistic budget limits", "Use envelope method", "Celebrate small wins" },
                    Priority = "medium"
                });
                break;
            case "subscription_management":
                insights.Add(new PersonalizedInsightDto
                {
                    InsightType = "subscription_optimization",
                    Title = "Subscription Cleanup",
                    Message = "Let's identify and optimize your recurring subscriptions to save money.",
                    RecommendedActions = new[] { "Audit all subscriptions", "Cancel unused services", "Look for family plan savings" },
                    Priority = "medium"
                });
                break;
        }

        return insights;
    }

    private static IEnumerable<PersonalizedInsightDto> GenerateSpendingPersonalityInsights(UserOnboardingQuiz quiz)
    {
        var insights = new List<PersonalizedInsightDto>();

        switch (quiz.SpendingPersonality)
        {
            case "impulse_buyer":
                insights.Add(new PersonalizedInsightDto
                {
                    InsightType = "impulse_control",
                    Title = "Impulse Spending Solutions",
                    Message = "We'll help you pause before purchases and make more mindful spending decisions.",
                    RecommendedActions = new[] { "Use 24-hour rule", "Remove stored payment methods", "Find free alternatives" },
                    Priority = "medium"
                });
                break;
            case "subscription_collector":
                insights.Add(new PersonalizedInsightDto
                {
                    InsightType = "subscription_awareness",
                    Title = "Subscription Mindfulness",
                    Message = "We'll help you keep track of all your subscriptions and their value to you.",
                    RecommendedActions = new[] { "Monthly subscription review", "Set subscription limits", "Use free trials wisely" },
                    Priority = "medium"
                });
                break;
        }

        return insights;
    }

    private static IEnumerable<PersonalizedInsightDto> GenerateSubscriptionInsights(UserOnboardingQuiz quiz)
    {
        var insights = new List<PersonalizedInsightDto>();

        if (quiz.SubscriptionAwareness == "no_idea" || quiz.SubscriptionAwareness == "unsure")
        {
            insights.Add(new PersonalizedInsightDto
            {
                InsightType = "subscription_discovery",
                Title = "Discover Hidden Subscriptions",
                Message = "We'll help you find all your subscriptions - you might be surprised by what you're paying for!",
                RecommendedActions = new[] { "Run automatic detection", "Review bank statements", "Check app store subscriptions" },
                Priority = "high"
            });
        }

        return insights;
    }

    private static IEnumerable<PersonalizedInsightDto> GenerateSavingsBarrierInsights(UserOnboardingQuiz quiz)
    {
        var insights = new List<PersonalizedInsightDto>();

        if (!string.IsNullOrEmpty(quiz.SavingsBarriers))
        {
            try
            {
                var barriers = JsonSerializer.Deserialize<string[]>(quiz.SavingsBarriers);
                if (barriers != null && barriers.Contains("bad_habits"))
                {
                    insights.Add(new PersonalizedInsightDto
                    {
                        InsightType = "habit_change",
                        Title = "Breaking Bad Financial Habits",
                        Message = "We'll help you identify and gradually replace expensive habits with budget-friendly alternatives.",
                        RecommendedActions = new[] { "Track habit triggers", "Find cheaper substitutes", "Set habit-breaking goals" },
                        Priority = "medium"
                    });
                }
            }
            catch (JsonException)
            {
                // If JSON parsing fails, skip this insight
            }
        }

        return insights;
    }
}
