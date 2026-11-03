using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Subscription;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Manages suggested subscriptions detected by the system.
/// Users can confirm, dismiss, or snooze these suggestions.
/// </summary>
[Authorize]
public class SuggestedSubscriptionsController : BaseController
{
  private readonly ISuggestedSubscriptionRepository _suggestedSubscriptionRepository;
  private readonly ICrowdsourcedSubscriptionPatternRepository _crowdsourcedPatternRepository;
  private readonly IDismissedSubscriptionPatternRepository _dismissedPatternRepository;
  private readonly ISubscriptionService _subscriptionService;
  private readonly ILogger<SuggestedSubscriptionsController> _logger;

  public SuggestedSubscriptionsController(
    ISuggestedSubscriptionRepository suggestedSubscriptionRepository,
    ICrowdsourcedSubscriptionPatternRepository crowdsourcedPatternRepository,
    IDismissedSubscriptionPatternRepository dismissedPatternRepository,
    ISubscriptionService subscriptionService,
    ILogger<SuggestedSubscriptionsController> logger)
  {
    _suggestedSubscriptionRepository = suggestedSubscriptionRepository;
    _crowdsourcedPatternRepository = crowdsourcedPatternRepository;
    _dismissedPatternRepository = dismissedPatternRepository;
    _subscriptionService = subscriptionService;
    _logger = logger;
  }

  /// <summary>
  /// Get all pending subscription suggestions for the current user.
  /// </summary>
  [HttpGet]
  public async Task<IActionResult> GetPendingSuggestions()
  {
    var userId = GetCurrentUserId();
    var suggestions = await _suggestedSubscriptionRepository.GetPendingByUserIdAsync(userId);

    var dtos = suggestions.Select(s => MapToDto(s)).ToList();
    return Ok(dtos);
  }

  /// <summary>
  /// Get all suggestions (including dismissed/confirmed) for the current user.
  /// </summary>
  [HttpGet("all")]
  public async Task<IActionResult> GetAllSuggestions()
  {
    var userId = GetCurrentUserId();
    var suggestions = await _suggestedSubscriptionRepository.GetByUserIdAsync(userId);

    var dtos = suggestions.Select(s => MapToDto(s)).ToList();
    return Ok(dtos);
  }

  /// <summary>
  /// Confirm a suggested subscription, creating a real tracked subscription.
  /// </summary>
  [HttpPost("{id}/confirm")]
  public async Task<IActionResult> ConfirmSuggestion(int id, [FromBody] ConfirmSuggestionRequest? request = null)
  {
    var userId = GetCurrentUserId();
    var suggestion = await _suggestedSubscriptionRepository.GetByIdAsync(id);

    if (suggestion == null || suggestion.UserId != userId)
    {
      return NotFound(new { message = "Suggestion not found" });
    }

    if (suggestion.Status != SuggestedSubscriptionStatus.Pending)
    {
      return BadRequest(new { message = $"Suggestion is already {suggestion.Status}" });
    }

    // Create a DetectedSubscriptionDto to confirm.
    // Category precedence: explicit request.Category → AI-suggested → (service falls back to the
    // merchant-name heuristic). We funnel the chosen category through AISuggestedCategory, which
    // ConfirmDetectedSubscriptionAsync honours over the heuristic.
    // Matched transaction IDs and the last-transaction date are carried through so the created
    // subscription is linked to its real transactions and gets a LastBilledDate (drives the
    // "possibly cancelled" logic) — previously both were dropped, leaving the sub unlinked.
    var detectedDto = new DetectedSubscriptionDto
    {
      MerchantName = suggestion.DisplayMerchantName,
      Amount = suggestion.Amount,
      Frequency = suggestion.Frequency,
      EstimatedNextDue = suggestion.PredictedNextDate ?? DateTime.UtcNow.AddMonths(1),
      Confidence = suggestion.Confidence,
      IsSubscriptionLikely = true,
      AISuggestedCategory = request?.Category ?? suggestion.AISuggestedCategory,
      AIInsight = suggestion.AIInsight,
      RelatedTransactionIds = SuggestedSubscription.ParseTransactionIds(suggestion.MatchedTransactionIds),
      LastTransactionDate = suggestion.LastTransactionDate
    };

    // Create the confirmed subscription
    var confirmedSubscription = await _subscriptionService.ConfirmDetectedSubscriptionAsync(userId, detectedDto);

    // Update the suggestion status
    suggestion.Status = SuggestedSubscriptionStatus.Confirmed;
    suggestion.ActionTakenAt = DateTime.UtcNow;
    suggestion.ConfirmedSubscriptionId = confirmedSubscription.Id;
    await _suggestedSubscriptionRepository.UpdateAsync(suggestion);

    // Update crowdsourced pattern (user opted in by default - anonymized data)
    await _crowdsourcedPatternRepository.CreateOrUpdateAsync(
      suggestion.NormalizedMerchantName,
      suggestion.Amount,
      (int)suggestion.Frequency,
      isConfirmation: true);

    _logger.LogInformation("User {UserId} confirmed suggestion {SuggestionId} as subscription {SubscriptionId}",
      userId, id, confirmedSubscription.Id);

    return Ok(confirmedSubscription);
  }

  /// <summary>
  /// Dismiss a suggested subscription. Permanent dismiss never re-suggests; default skips 60 days.
  /// </summary>
  [HttpPost("{id}/dismiss")]
  public async Task<IActionResult> DismissSuggestion(int id, [FromBody] DismissSuggestionRequest? request = null)
  {
    var userId = GetCurrentUserId();
    var suggestion = await _suggestedSubscriptionRepository.GetByIdAsync(id);

    if (suggestion == null || suggestion.UserId != userId)
    {
      return NotFound(new { message = "Suggestion not found" });
    }

    if (suggestion.Status == SuggestedSubscriptionStatus.Confirmed)
    {
      return BadRequest(new { message = "Cannot dismiss a confirmed subscription" });
    }

    var isPermanent = request?.Permanent ?? false;

    // Update the suggestion status
    suggestion.Status = SuggestedSubscriptionStatus.Dismissed;
    suggestion.ActionTakenAt = DateTime.UtcNow;
    suggestion.DismissReason = isPermanent
      ? $"Permanently dismissed: {request?.Reason ?? "user choice"}"
      : request?.Reason;
    await _suggestedSubscriptionRepository.UpdateAsync(suggestion);

    // Add to dismissed patterns
    await _dismissedPatternRepository.CreateAsync(new DismissedSubscriptionPattern
    {
      UserId = userId,
      MerchantName = suggestion.NormalizedMerchantName,
      AmountMin = suggestion.Amount - 3,
      AmountMax = suggestion.Amount + 3,
      DismissReason = request?.Reason,
      IsPermanent = isPermanent
    });

    // Update crowdsourced pattern (user opted in by default - anonymized data)
    await _crowdsourcedPatternRepository.CreateOrUpdateAsync(
      suggestion.NormalizedMerchantName,
      suggestion.Amount,
      (int)suggestion.Frequency,
      isConfirmation: false);

    _logger.LogInformation("User {UserId} dismissed suggestion {SuggestionId} with reason: {Reason}",
      userId, id, request?.Reason ?? "none");

    return Ok(new { message = "Suggestion dismissed", suggestionId = id });
  }

  /// <summary>
  /// Snooze a suggested subscription for a specified number of days.
  /// </summary>
  [HttpPost("{id}/snooze")]
  public async Task<IActionResult> SnoozeSuggestion(int id, [FromBody] SnoozeSuggestionRequest request)
  {
    var userId = GetCurrentUserId();
    var suggestion = await _suggestedSubscriptionRepository.GetByIdAsync(id);

    if (suggestion == null || suggestion.UserId != userId)
    {
      return NotFound(new { message = "Suggestion not found" });
    }

    if (suggestion.Status == SuggestedSubscriptionStatus.Confirmed)
    {
      return BadRequest(new { message = "Cannot snooze a confirmed subscription" });
    }

    var snoozeDays = request.Days > 0 ? request.Days : 30; // Default 30 days
    snoozeDays = Math.Min(snoozeDays, 90); // Max 90 days

    suggestion.Status = SuggestedSubscriptionStatus.Snoozed;
    suggestion.SnoozeUntil = DateTime.UtcNow.AddDays(snoozeDays);
    suggestion.ActionTakenAt = DateTime.UtcNow;
    await _suggestedSubscriptionRepository.UpdateAsync(suggestion);

    _logger.LogInformation("User {UserId} snoozed suggestion {SuggestionId} until {SnoozeUntil}",
      userId, id, suggestion.SnoozeUntil);

    return Ok(new {
      message = "Suggestion snoozed",
      suggestionId = id,
      snoozeUntil = suggestion.SnoozeUntil
    });
  }

  /// <summary>
  /// Undo a dismissed or snoozed suggestion, returning it to pending status.
  /// </summary>
  [HttpPost("{id}/undo")]
  public async Task<IActionResult> UndoSuggestionAction(int id)
  {
    var userId = GetCurrentUserId();
    var suggestion = await _suggestedSubscriptionRepository.GetByIdAsync(id);

    if (suggestion == null || suggestion.UserId != userId)
    {
      return NotFound(new { message = "Suggestion not found" });
    }

    if (suggestion.Status == SuggestedSubscriptionStatus.Confirmed)
    {
      return BadRequest(new { message = "Cannot undo a confirmed subscription. Delete the subscription instead." });
    }

    if (suggestion.Status == SuggestedSubscriptionStatus.Pending)
    {
      return BadRequest(new { message = "Suggestion is already pending" });
    }

    // Remove from dismissed patterns if it was dismissed
    if (suggestion.Status == SuggestedSubscriptionStatus.Dismissed)
    {
      var dismissedPattern = await _dismissedPatternRepository.GetByMerchantNameAsync(userId, suggestion.NormalizedMerchantName);
      if (dismissedPattern != null)
      {
        await _dismissedPatternRepository.DeleteAsync(dismissedPattern.Id);
      }
    }

    suggestion.Status = SuggestedSubscriptionStatus.Pending;
    suggestion.SnoozeUntil = null;
    suggestion.ActionTakenAt = null;
    suggestion.DismissReason = null;
    await _suggestedSubscriptionRepository.UpdateAsync(suggestion);

    _logger.LogInformation("User {UserId} undid action on suggestion {SuggestionId}", userId, id);

    return Ok(MapToDto(suggestion));
  }

  /// <summary>
  /// Toggle the HiddenFromReadOnly flag for a suggestion (for gift hiding, etc.)
  /// </summary>
  [HttpPatch("{id}/visibility")]
  public async Task<IActionResult> UpdateVisibility(int id, [FromBody] UpdateVisibilityRequest request)
  {
    var userId = GetCurrentUserId();
    var suggestion = await _suggestedSubscriptionRepository.GetByIdAsync(id);

    if (suggestion == null || suggestion.UserId != userId)
    {
      return NotFound(new { message = "Suggestion not found" });
    }

    suggestion.HiddenFromReadOnly = request.HiddenFromReadOnly;
    await _suggestedSubscriptionRepository.UpdateAsync(suggestion);

    _logger.LogInformation("User {UserId} set suggestion {SuggestionId} visibility to HiddenFromReadOnly={Hidden}",
      userId, id, request.HiddenFromReadOnly);

    return Ok(MapToDto(suggestion));
  }

  private static SuggestedSubscriptionDto MapToDto(SuggestedSubscription suggestion)
  {
    return new SuggestedSubscriptionDto
    {
      Id = suggestion.Id,
      MerchantName = suggestion.DisplayMerchantName,
      Amount = suggestion.Amount,
      Frequency = suggestion.Frequency,
      Confidence = suggestion.Confidence,
      BaseConfidence = suggestion.BaseConfidence,
      CrowdsourceBoost = suggestion.CrowdsourceBoost,
      Status = suggestion.Status,
      SnoozeUntil = suggestion.SnoozeUntil,
      HiddenFromReadOnly = suggestion.HiddenFromReadOnly,
      AISuggestedCategory = suggestion.AISuggestedCategory,
      AIInsight = suggestion.AIInsight,
      MatchedTransactionCount = suggestion.MatchedTransactionCount,
      PredictedNextDate = suggestion.PredictedNextDate,
      DetectedAt = suggestion.DetectedAt,
      ActionTakenAt = suggestion.ActionTakenAt
    };
  }
}

// Request DTOs
public class ConfirmSuggestionRequest
{
  public SubscriptionCategory? Category { get; set; }
}

public class DismissSuggestionRequest
{
  public string? Reason { get; set; }
  /// <summary>
  /// If true, permanently dismiss this merchant — it will never be re-suggested.
  /// If false (default), the merchant is skipped for 60 days then may reappear.
  /// </summary>
  public bool Permanent { get; set; }
}

public class SnoozeSuggestionRequest
{
  public int Days { get; set; } = 30;
}

public class UpdateVisibilityRequest
{
  public bool HiddenFromReadOnly { get; set; }
}

// Response DTO
public class SuggestedSubscriptionDto
{
  public int Id { get; set; }
  public string MerchantName { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public SubscriptionFrequency Frequency { get; set; }
  public double Confidence { get; set; }
  public double BaseConfidence { get; set; }
  public double CrowdsourceBoost { get; set; }
  public SuggestedSubscriptionStatus Status { get; set; }
  public DateTime? SnoozeUntil { get; set; }
  public bool HiddenFromReadOnly { get; set; }
  public SubscriptionCategory? AISuggestedCategory { get; set; }
  public string? AIInsight { get; set; }
  public int MatchedTransactionCount { get; set; }
  public DateTime? PredictedNextDate { get; set; }
  public DateTime DetectedAt { get; set; }
  public DateTime? ActionTakenAt { get; set; }
}
