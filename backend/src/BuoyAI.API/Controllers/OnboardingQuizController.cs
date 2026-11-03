using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;
using System.Security.Claims;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Controller for managing user onboarding quiz functionality
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class OnboardingQuizController : BaseController
{
    private readonly IOnboardingQuizService _quizService;
    private readonly ILogger<OnboardingQuizController> _logger;

    public OnboardingQuizController(
        IOnboardingQuizService quizService,
        ILogger<OnboardingQuizController> logger)
    {
        _quizService = quizService;
        _logger = logger;
    }

    /// <summary>
    /// Submit onboarding quiz responses
    /// </summary>
    [HttpPost("submit")]
    public async Task<IActionResult> SubmitQuiz([FromBody] OnboardingQuizSubmissionDto submission)
    {
        try
        {
            var userId = GetCurrentUserId();
            var result = await _quizService.SubmitQuizAsync(userId, submission);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Get the latest quiz response for the current user
    /// </summary>
    [HttpGet("latest")]
    public async Task<IActionResult> GetLatestQuiz()
    {
        var userId = GetCurrentUserId();
        var result = await _quizService.GetLatestQuizAsync(userId);
        if (result == null)
        {
            return NotFound(new { message = "No quiz responses found" });
        }

        return Ok(result);
    }

    /// <summary>
    /// Get quiz history for the current user
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetQuizHistory()
    {
        var userId = GetCurrentUserId();
        var result = await _quizService.GetQuizHistoryAsync(userId);
        return Ok(result);
    }

    /// <summary>
    /// Check if the current user has completed the onboarding quiz
    /// </summary>
    [HttpGet("completed")]
    public async Task<IActionResult> HasCompletedQuiz()
    {
        var userId = GetCurrentUserId();
        var hasCompleted = await _quizService.HasCompletedQuizAsync(userId);
        return Ok(new { hasCompleted });
    }

    /// <summary>
    /// Get personalized insights based on quiz responses
    /// </summary>
    [HttpGet("insights")]
    public async Task<IActionResult> GetPersonalizedInsights()
    {
        var userId = GetCurrentUserId();
        var insights = await _quizService.GeneratePersonalizedInsightsAsync(userId);
        return Ok(insights);
    }


}
