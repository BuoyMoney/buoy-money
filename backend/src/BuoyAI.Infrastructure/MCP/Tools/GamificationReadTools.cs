using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.DTOs.Gamification;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tools for reading gamification data: level, streaks, achievements, and — the part
/// the AI advisor should actually reason about — the cautionary ("oops") badge history
/// with repeat counts. A user may believe their budget is sound while repeatedly earning
/// Selective Amnesia (rollover-debt resets); that contradiction is advisory gold.
/// </summary>
[McpServerToolType]
public sealed class GamificationReadTools
{
    private readonly IGamificationService _gamificationService;
    private readonly ILogger<GamificationReadTools> _logger;

    public GamificationReadTools(
        IGamificationService gamificationService,
        ILogger<GamificationReadTools> logger)
    {
        _gamificationService = gamificationService;
        _logger = logger;
    }

    [McpServerTool(Name = "get_gamification_summary", ReadOnly = true)]
    [Description("Get the user's gamification state: level, points, active streaks, recent achievements, and cautionary ('oops') badge history with repeat counts plus derived behavioral signals. The oops repeat counts are revealed-behavior evidence (e.g. repeatedly earning Selective Amnesia means the user keeps resetting rollover debt, suggesting allocations are chronically too low even if the user believes the budget is fine). Use this when discussing budget health, habits, streaks, badges, or when the user's self-assessment may not match their behavior.")]
    public async Task<string> GetGamificationSummaryAsync(
        [Description("User ID to get the gamification summary for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_gamification_summary called for user {UserId}", userId);

            var achievements = await _gamificationService.GetAchievementsAsync(userId);
            var streaks = await _gamificationService.GetStreaksAsync(userId);
            var oops = await _gamificationService.GetOopsBadgeHistoryAsync(userId);

            var totalPoints = achievements.Sum(a => a.Points);
            var (level, pointsToNext) = LevelDefinitions.GetLevel(totalPoints);
            var signals = OopsBadgeSignals.Build(
                oops.Select(o => (o.Key, o.TimesEarned, o.LastEarnedAt, o.CurrentlyHeld)).ToList());

            var result = new
            {
                Level = level,
                TotalPoints = totalPoints,
                PointsToNextLevel = pointsToNext,
                ActiveStreaks = streaks
                    .Where(s => s.CurrentCount > 0)
                    .Select(s => new { s.DisplayName, s.CurrentCount, s.BestCount, s.IsAtRisk }),
                RecentAchievements = achievements
                    .Where(a => !AchievementDefinitions.IsNegative(a.AchievementKey))
                    .Take(5)
                    .Select(a => new { a.Title, EarnedAt = a.EarnedAt.ToString("yyyy-MM-dd") }),
                // Includes badges whose condition has since been fixed (revoked) — the
                // repeat history is the signal, not just the current state.
                OopsBadgeHistory = oops.Select(o => new
                {
                    o.Key,
                    o.Title,
                    o.TimesEarned,
                    LastEarnedAt = o.LastEarnedAt?.ToString("yyyy-MM-dd"),
                    o.CurrentlyHeld
                }),
                BehavioralSignals = signals,
                Note = signals.Count > 0
                    ? "Behavioral signals are revealed-behavior evidence and may contradict the user's stated beliefs about their budget. Weigh them when giving advice."
                    : "No repeat-slip-up signals in the badge history."
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_gamification_summary for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }
}
