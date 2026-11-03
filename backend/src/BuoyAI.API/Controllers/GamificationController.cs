using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GamificationController : BaseController
{
  private readonly IGamificationService _gamificationService;
  private readonly ILogger<GamificationController> _logger;

  public GamificationController(
    IGamificationService gamificationService,
    ILogger<GamificationController> logger)
  {
    _gamificationService = gamificationService;
    _logger = logger;
  }

  /// <summary>
  /// Get the gamification dashboard data for the current user
  /// </summary>
  [HttpGet("dashboard")]
  public async Task<IActionResult> GetDashboard()
  {
    var userId = GetCurrentUserId();
    var dashboard = await _gamificationService.GetDashboardDataAsync(userId);
    return Ok(dashboard);
  }

  /// <summary>
  /// Get all achievements for the current user
  /// </summary>
  [HttpGet("achievements")]
  public async Task<IActionResult> GetAchievements()
  {
    var userId = GetCurrentUserId();
    var achievements = await _gamificationService.GetAchievementsAsync(userId);
    return Ok(achievements);
  }

  /// <summary>
  /// Get the full achievement catalog (earned + locked) for the current user. Locked
  /// "good" achievements come back as masked silhouettes; cautionary ("oops")
  /// achievements are always fully described. Powers the Achievements page.
  /// </summary>
  [HttpGet("catalog")]
  public async Task<IActionResult> GetAchievementCatalog()
  {
    var userId = GetCurrentUserId();
    var catalog = await _gamificationService.GetAchievementCatalogAsync(userId);
    return Ok(catalog);
  }

  /// <summary>
  /// Mark achievements as seen (no longer new)
  /// </summary>
  [HttpPost("achievements/seen")]
  public async Task<IActionResult> MarkAchievementsSeen([FromBody] List<string> achievementKeys)
  {
    var userId = GetCurrentUserId();
    await _gamificationService.MarkAchievementsSeenAsync(userId, achievementKeys);
    return NoContent();
  }

  /// <summary>
  /// Get all streaks for the current user
  /// </summary>
  [HttpGet("streaks")]
  public async Task<IActionResult> GetStreaks()
  {
    var userId = GetCurrentUserId();
    var streaks = await _gamificationService.GetStreaksAsync(userId);
    return Ok(streaks);
  }

  /// <summary>
  /// Get active challenges for the current user
  /// </summary>
  [HttpGet("challenges")]
  public async Task<IActionResult> GetChallenges()
  {
    var userId = GetCurrentUserId();
    var challenges = await _gamificationService.GetActiveChallengesAsync(userId);
    return Ok(challenges);
  }

  /// <summary>
  /// Generate a daily challenge for the current user
  /// </summary>
  [HttpPost("challenges/generate")]
  public async Task<IActionResult> GenerateChallenge()
  {
    var userId = GetCurrentUserId();
    var challenge = await _gamificationService.GenerateDailyChallengeAsync(userId);
    return Ok(challenge);
  }

  /// <summary>
  /// Dismiss (delete) a challenge
  /// </summary>
  [HttpDelete("challenges/{challengeId}")]
  public async Task<IActionResult> DismissChallenge(int challengeId)
  {
    var userId = GetCurrentUserId();
    await _gamificationService.DismissChallengeAsync(userId, challengeId);
    return NoContent();
  }

  /// <summary>
  /// Check and award any new achievements (triggered after significant actions)
  /// </summary>
  [HttpPost("check-achievements")]
  public async Task<IActionResult> CheckAchievements()
  {
    var userId = GetCurrentUserId();
    await _gamificationService.CheckAndAwardAchievementsAsync(userId);
    var dashboard = await _gamificationService.GetDashboardDataAsync(userId);
    return Ok(dashboard);
  }

  /// <summary>
  /// Advance the current user's active challenge of the given type (e.g. after
  /// categorizing a transaction or reviewing the budget). Marks it completed at target.
  /// </summary>
  [HttpPost("challenges/{challengeType}/progress")]
  public async Task<IActionResult> IncrementChallengeProgress(string challengeType)
  {
    var userId = GetCurrentUserId();
    await _gamificationService.IncrementChallengeProgressAsync(userId, challengeType);
    return NoContent();
  }

  /// <summary>
  /// Update streak progress (called daily or after budget reviews)
  /// </summary>
  [HttpPost("update-streaks")]
  public async Task<IActionResult> UpdateStreaks()
  {
    var userId = GetCurrentUserId();
    await _gamificationService.UpdateStreaksAsync(userId);
    var streaks = await _gamificationService.GetStreaksAsync(userId);
    return Ok(streaks);
  }
}
