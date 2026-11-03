using BuoyAI.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Subscription;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Enums;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Services.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuoyAI.Infrastructure.Services;

public partial class SubscriptionService : ISubscriptionService
{
  // Compiled regex patterns for merchant name normalization (performance optimization)
  [GeneratedRegex(@"\s+[A-Z0-9]{6,}$", RegexOptions.Compiled)]
  private static partial Regex TransactionIdPattern();
  
  [GeneratedRegex(@"\s+\d{4,}$", RegexOptions.Compiled)]
  private static partial Regex TrailingNumbersPattern();
  
  [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
  private static partial Regex WhitespacePattern();
  
  private readonly ISubscriptionRepository _subscriptionRepository;
  private readonly ISubscriptionTransactionRepository _subscriptionTransactionRepository;
  private readonly ITransactionRepository _transactionRepository;
  private readonly IDismissedSubscriptionPatternRepository _dismissedPatternRepository;
  private readonly ISuggestedSubscriptionRepository _suggestedSubscriptionRepository;
  private readonly ICrowdsourcedSubscriptionPatternRepository _crowdsourcedPatternRepository;
  private readonly IEntitlementService _entitlements;
  private readonly IUserRepository _userRepository;
  private readonly ILogger<SubscriptionService> _logger;
  private readonly IAiProvider _ai;
  private readonly Dictionary<SubscriptionTier, string[]> _modelTiers;

  public SubscriptionService(
    ISubscriptionRepository subscriptionRepository,
    ISubscriptionTransactionRepository subscriptionTransactionRepository,
    ITransactionRepository transactionRepository,
    IDismissedSubscriptionPatternRepository dismissedPatternRepository,
    ISuggestedSubscriptionRepository suggestedSubscriptionRepository,
    ICrowdsourcedSubscriptionPatternRepository crowdsourcedPatternRepository,
    IEntitlementService entitlements,
    IUserRepository userRepository,
    IConfiguration configuration,
    ILogger<SubscriptionService> logger,
    IAiProvider? aiProvider = null)
  {
    _subscriptionRepository = subscriptionRepository;
    _subscriptionTransactionRepository = subscriptionTransactionRepository;
    _transactionRepository = transactionRepository;
    _dismissedPatternRepository = dismissedPatternRepository;
    _suggestedSubscriptionRepository = suggestedSubscriptionRepository;
    _crowdsourcedPatternRepository = crowdsourcedPatternRepository;
    _entitlements = entitlements;
    _userRepository = userRepository;
    _logger = logger;
    
    var geminiSettings = configuration.GetSection("Gemini");
    _ai = aiProvider ?? new GeminiNativeProvider(configuration, NullLogger<GeminiNativeProvider>.Instance, null);
    
    // Note: API key validation is done at method level for AI-enhanced features
    // This allows basic subscription CRUD operations even without AI configuration
    
    // Load model tiers from configuration
    _modelTiers = new Dictionary<SubscriptionTier, string[]>
    {
      [SubscriptionTier.Standard] = new[]
      {
        geminiSettings["Models:Free:Primary"] ?? "gemini-2.0-flash",
        geminiSettings["Models:Free:Fallback"] ?? "gemini-2.0-flash-lite"
      },
      [SubscriptionTier.Premium] = new[]
      {
        geminiSettings["Models:Pro:Primary"] ?? "gemini-2.5-flash",
        geminiSettings["Models:Pro:Secondary"] ?? "gemini-2.5-flash"
      },
      [SubscriptionTier.Admin] = new[]
      {
        geminiSettings["Models:Pro:Primary"] ?? "gemini-2.5-flash",
        geminiSettings["Models:Pro:Secondary"] ?? "gemini-2.5-flash"
      }
    };
  }

  public async Task<IEnumerable<SubscriptionDto>> GetSubscriptionsAsync(int userId)
  {
    try
    {
      var subscriptions = await _subscriptionRepository.GetByUserIdAsync(userId);
      return subscriptions.Select(MapToDto);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting subscriptions for user {UserId}", userId);
      throw;
    }
  }

  public async Task<SubscriptionDto?> GetSubscriptionByIdAsync(int userId, int subscriptionId)
  {
    try
    {
      var subscription = await _subscriptionRepository.GetByIdAsync(subscriptionId);
      if (subscription == null || subscription.UserId != userId)
      {
        return null;
      }
      return MapToDto(subscription);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting subscription {SubscriptionId} for user {UserId}", subscriptionId, userId);
      throw;
    }
  }

  public async Task<SubscriptionDto> CreateSubscriptionAsync(int userId, CreateSubscriptionRequest request)
  {
    try
    {
      // Price must be positive and within the $1B cap (QE #615: negatives/$0 were
      // accepted and a ~$1T amount 500'd on the decimal→DB write).
      Core.Services.MonetaryLimits.ValidatePositive(request.Amount, "Amount");

      // Ensure DateTime is in UTC format for PostgreSQL timestamptz
      var nextDueDate = request.NextDueDate;
      if (nextDueDate.Kind == DateTimeKind.Unspecified)
        nextDueDate = DateTime.SpecifyKind(nextDueDate, DateTimeKind.Utc);
      else if (nextDueDate.Kind == DateTimeKind.Local)
        nextDueDate = nextDueDate.ToUniversalTime();
      
      var subscription = new Subscription
      {
        UserId = userId,
        Name = request.Name,
        Description = request.Description ?? string.Empty,
        Amount = request.Amount,
        Frequency = request.Frequency,
        NextDueDate = nextDueDate,
        Category = request.Category,
        MerchantName = request.MerchantName,
        Status = SubscriptionStatus.Active,
        IsDetected = false
      };

      var createdSubscription = await _subscriptionRepository.CreateAsync(subscription);
      _logger.LogInformation("Created subscription {SubscriptionId} for user {UserId}", createdSubscription.Id, userId);
      
      return MapToDto(createdSubscription);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error creating subscription for user {UserId}", userId);
      throw;
    }
  }

  public async Task<SubscriptionDto> UpdateSubscriptionAsync(int userId, int subscriptionId, UpdateSubscriptionRequest request)
  {
    try
    {
      var subscription = await _subscriptionRepository.GetByIdAsync(subscriptionId);
      if (subscription == null || subscription.UserId != userId)
      {
        throw new ArgumentException("Subscription not found or access denied");
      }

      // Update properties if provided
      if (!string.IsNullOrEmpty(request.Name))
        subscription.Name = request.Name;
      if (!string.IsNullOrEmpty(request.Description))
        subscription.Description = request.Description;
      if (request.Amount.HasValue)
      {
        Core.Services.MonetaryLimits.ValidatePositive(request.Amount.Value, "Amount");
        subscription.Amount = request.Amount.Value;
      }
      if (request.Frequency.HasValue)
        subscription.Frequency = request.Frequency.Value;
      if (request.NextDueDate.HasValue)
      {
        // Ensure DateTime is in UTC format for PostgreSQL timestamptz
        var nextDueDate = request.NextDueDate.Value;
        subscription.NextDueDate = nextDueDate.Kind == DateTimeKind.Unspecified 
          ? DateTime.SpecifyKind(nextDueDate, DateTimeKind.Utc)
          : nextDueDate.ToUniversalTime();
      }
      if (request.Status.HasValue)
        subscription.Status = request.Status.Value;
      if (request.Category.HasValue)
        subscription.Category = request.Category.Value;
      if (request.HiddenFromReadOnly.HasValue)
        subscription.HiddenFromReadOnly = request.HiddenFromReadOnly.Value;

      var updatedSubscription = await _subscriptionRepository.UpdateAsync(subscription);
      _logger.LogInformation("Updated subscription {SubscriptionId} for user {UserId}", subscriptionId, userId);
      
      return MapToDto(updatedSubscription);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error updating subscription {SubscriptionId} for user {UserId}", subscriptionId, userId);
      throw;
    }
  }

  public async Task DeleteSubscriptionAsync(int userId, int subscriptionId)
  {
    try
    {
      var subscription = await _subscriptionRepository.GetByIdAsync(subscriptionId);
      if (subscription == null || subscription.UserId != userId)
      {
        throw new ArgumentException("Subscription not found or access denied");
      }

      // Delete related subscription-transaction relationships
      await _subscriptionTransactionRepository.DeleteBySubscriptionIdAsync(subscriptionId);
      
      // Delete the subscription
      await _subscriptionRepository.DeleteAsync(subscriptionId);
      
      _logger.LogInformation("Deleted subscription {SubscriptionId} for user {UserId}", subscriptionId, userId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error deleting subscription {SubscriptionId} for user {UserId}", subscriptionId, userId);
      throw;
    }
  }

  public async Task<SubscriptionDetectionResponse> DetectSubscriptionsAsync(int userId, SubscriptionDetectionRequest? request = null, bool isOnDemand = false)
  {
    try
    {
      // Pattern-based detection runs unconditionally — it does NOT need Gemini. Only the
      // optional AI enhancement below is gated on a key (and it is already try/catch-guarded),
      // so a missing key simply means IsAIEnhanced stays false rather than failing detection.
      request ??= new SubscriptionDetectionRequest();
      var startDate = request.StartDate ?? DateTime.UtcNow.AddYears(-2);
      var endDate = request.EndDate ?? DateTime.UtcNow;

      _logger.LogInformation("Detecting subscriptions for user {UserId} from {StartDate} to {EndDate}", 
        userId, startDate, endDate);

      // Get user and tier for AI enhancement
      var user = await _userRepository.GetByIdAsync(userId);
      var tier = await _entitlements.GetEffectiveTierAsync(userId) ?? SubscriptionTier.Standard;
      var isPremium = tier.HasPremiumAccess();

      var transactions = await _transactionRepository.GetByUserIdAsync(userId, startDate, endDate);
      
      // Get existing subscriptions to exclude already-tracked merchants
      var existingSubscriptions = await _subscriptionRepository.GetByUserIdAsync(userId);
      var existingMerchants = existingSubscriptions
        .Where(s => !string.IsNullOrEmpty(s.MerchantName))
        .Select(s => NormalizeMerchantName(s.MerchantName!))
        .ToHashSet();
      
      var detectedSubscriptions = new List<DetectedSubscriptionDto>();

      // Pre-filter: Remove duplicate transactions (same merchant, similar amount, within 2 days)
      // This handles Plaid sync duplicates that create 0-1 day intervals
      var filteredTransactions = RemoveDuplicateTransactions(transactions.ToList());

      // Group by normalized merchant, then cluster each merchant's charges by amount within a
      // PROPORTIONAL tolerance of the group median. The old approach snapped amounts to fixed
      // ~$5 buckets, which shattered variable/price-changed subs across buckets (e.g. $15.99 and
      // $17.99 landed in different $5 buckets) and merged unrelated charges that happened to
      // round into the same bucket. Clustering keeps a price change as one group and keeps
      // genuinely different charges apart.
      //
      // The candidate filter is IsSubscriptionCandidate() (budgetable expense, excluding
      // transfers, credit-card payments, rent/mortgage and loan payments) rather than the old
      // IsExpense(), which swept in the most-regular debits and mislabeled them as subscriptions.
      var potentialSubscriptions = filteredTransactions
        .Where(t => t.IsSubscriptionCandidate() && !string.IsNullOrEmpty(t.MerchantName))
        .GroupBy(t => NormalizeMerchantName(t.MerchantName))
        .Where(g => !existingMerchants.Contains(g.Key)) // Exclude already-tracked
        .SelectMany(merchantGroup => ClusterByAmount(merchantGroup.ToList()))
        // Normally require MinOccurrences (>=3) charges. The single exception is a genuinely
        // ANNUAL cadence: an annual sub only bills once a year, so a 2-year window holds at most
        // 2-3 charges and often exactly 2. Let a 2-charge cluster through ONLY when its single
        // interval is ~365 days (see IsAnnualTwoChargePattern) — the near-year spacing is what
        // keeps this from resurrecting the "two random purchases = subscription" false positive.
        .Where(cluster => cluster.Count >= request.MinOccurrences || IsAnnualTwoChargePattern(cluster))
        .ToList();

      // Batch-load dismissal + crowdsource data ONCE up front instead of issuing two DB round-trips
      // per merchant group inside the loop (N+1). This foreach runs for every user in the weekly
      // background sweep, so the per-group queries multiplied across the whole user base.
      var candidateMerchantKeys = potentialSubscriptions
        .Select(g => NormalizeMerchantName(g.First().MerchantName).ToLowerInvariant().Trim())
        .Distinct()
        .ToList();
      // DismissedSubscriptionPattern.MerchantName is stored lower-cased/trimmed (see repo CreateAsync).
      var activeDismissedByMerchant = (await _dismissedPatternRepository.GetActiveDismissedAsync(userId, skipDays: 60))
        .ToLookup(d => d.MerchantName);
      var crowdsourcedByMerchant = await _crowdsourcedPatternRepository.GetByMerchantNamesAsync(candidateMerchantKeys);

      foreach (var group in potentialSubscriptions)
      {
        var merchant = NormalizeMerchantName(group.First().MerchantName);
        var merchantKey = merchant.ToLowerInvariant().Trim();
        var groupAmount = group.First().Amount;

        // Check if pattern was previously dismissed (with 60-day skip period) — evaluated in-memory
        // against the pre-fetched active dismissals (same predicate as IsPatternDismissedAsync).
        var isDismissed = activeDismissedByMerchant[merchantKey].Any(d =>
          !d.AmountMin.HasValue || // No amount constraint, dismiss all from this merchant
          (d.AmountMin <= groupAmount && d.AmountMax >= groupAmount)); // Amount falls in dismissed range
        if (isDismissed)
        {
          _logger.LogDebug("Skipping dismissed pattern: {Merchant}", merchant);
          continue;
        }

        var pattern = AnalyzeTransactionPattern(group, request);
        if (pattern != null && pattern.IsSubscriptionLikely)
        {
          // Detect price changes
          DetectPriceChanges(pattern, group);

          // Look up the crowdsourced pattern and RECORD the boost for transparency only.
          // The boost is applied exactly once — when the suggestion is persisted
          // (StoreSuggestedSubscriptionsAsync), where BaseConfidence stays the pure pre-boost
          // score. Folding it into pattern.Confidence here as well would double-count it.
          crowdsourcedByMerchant.TryGetValue(merchantKey, out var crowdsourcedPattern);
          if (crowdsourcedPattern != null && crowdsourcedPattern.ConfidenceBoost > 0)
          {
            if (pattern.ConfidenceDetails != null)
              pattern.ConfidenceDetails.CrowdsourceBoost = crowdsourcedPattern.ConfidenceBoost;
            pattern.DetectionMethod = $"pattern-based + crowdsource boost ({crowdsourcedPattern.ConfidenceBoost:+0.00})";
            _logger.LogDebug(
              "Recorded crowdsource boost of {Boost} for {Merchant} (confirmations: {Confirms}, dismissals: {Dismissals})",
              crowdsourcedPattern.ConfidenceBoost, merchant,
              crowdsourcedPattern.ConfirmationCount, crowdsourcedPattern.DismissalCount);
          }

          detectedSubscriptions.Add(pattern);
        }
      }

      // AI Enhancement for Premium users who agreed to share data with Gemini (5.1.2(i)).
      // Without consent the pattern-based results stand on their own.
      if (isPremium && detectedSubscriptions.Count > 0 && _ai.IsConfigured
          && AiConsent.IsCurrent(user))
      {
        try
        {
          detectedSubscriptions = await EnhanceWithAIAsync(userId, detectedSubscriptions, tier);
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "AI enhancement failed, using pattern-based detection only");
        }
      }

      // Store detections in SuggestedSubscriptions table for persistence
      await StoreSuggestedSubscriptionsAsync(userId, user?.FamilyId, detectedSubscriptions);

      // Record the on-demand cooldown clock ONLY for user-initiated runs. The weekly background
      // job (SubscriptionDetectionService) calls this method for every user with isOnDemand=false;
      // stamping the timestamp there would leave every user's "Detect" button stuck on cooldown
      // for a week after each weekly sweep. This is authoritative even when the run produced zero
      // suggestions (unlike inferring from suggestion DetectedAt).
      if (isOnDemand && user != null)
      {
        user.LastSubscriptionDetectionRunAt = DateTime.UtcNow;
        await _userRepository.UpdateAsync(user);
      }

      var response = new SubscriptionDetectionResponse
      {
        DetectedSubscriptions = detectedSubscriptions,
        TotalDetected = detectedSubscriptions.Count,
        HighConfidenceCount = detectedSubscriptions.Count(d => d.Confidence >= 0.85),
        PendingReviewCount = detectedSubscriptions.Count(d => d.Confidence < 0.85),
        IsAIEnhanced = isPremium && detectedSubscriptions.Any(d => d.IsAIEnhanced),
        DetectionDate = DateTime.UtcNow
      };

      _logger.LogInformation("Detected {Count} potential subscriptions for user {UserId} (AI Enhanced: {IsAI})", 
        detectedSubscriptions.Count, userId, response.IsAIEnhanced);

      return response;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error detecting subscriptions for user {UserId}", userId);
      throw;
    }
  }

  private void DetectPriceChanges(DetectedSubscriptionDto pattern, List<Transaction> transactions)
  {
    if (transactions.Count < 2) return;

    // Use absolute amounts consistently — expenses are stored as positive magnitudes here, but
    // guarding with Math.Abs keeps the sign of the delta meaningful regardless of storage.
    var sortedByDate = transactions.OrderBy(t => t.Date).ToList();
    var latestAmount = Math.Abs(sortedByDate.Last().Amount);
    var previousAmounts = sortedByDate.Take(sortedByDate.Count - 1).Select(t => Math.Abs(t.Amount)).ToList();
    var avgPreviousAmount = previousAmounts.Average();

    // Guard divide-by-zero: a $0 previous average (e.g. all-refund history) can't yield a percent.
    if (avgPreviousAmount == 0) return;

    if (Math.Abs(latestAmount - avgPreviousAmount) > 0.50m) // More than 50 cents difference
    {
      pattern.PreviousAmount = avgPreviousAmount;
      pattern.PriceChangeAmount = latestAmount - avgPreviousAmount;
      pattern.PriceChangePercent = (double)((latestAmount - avgPreviousAmount) / avgPreviousAmount * 100);
    }
  }

  private async Task<List<DetectedSubscriptionDto>> EnhanceWithAIAsync(
    int userId, 
    List<DetectedSubscriptionDto> detectedSubscriptions,
    SubscriptionTier tier)
  {
    var models = _modelTiers[tier.ForLimits()];
    var modelName = models[0];
    using var usageScope = AiUsageScope.ForUser(userId);
    
    _logger.LogDebug("Enhancing {Count} subscriptions with AI (model: {Model})", 
      detectedSubscriptions.Count, modelName);

    // Build prompt with detected subscriptions
    var subscriptionList = string.Join("\n", detectedSubscriptions.Select((s, i) => 
      $"{i + 1}. {s.MerchantName}: ${s.Amount}/month, {s.Frequency}, Confidence: {s.Confidence:P0}" +
      (s.PriceChangeAmount.HasValue ? $" (Price changed ${s.PriceChangeAmount:+0.00;-0.00})" : "")));

    var prompt = $@"You are a financial advisor AI analyzing detected subscription patterns.

For each subscription below, provide:
1. A suggested category (one of: Streaming, Software, Utilities, Fitness, News, Gaming, Food, Shopping, Insurance, Other)
2. A brief insight (max 50 chars) - especially note price changes, unusual patterns, or money-saving tips
3. Whether this is likely a real subscription (true/false)

Detected Subscriptions:
{subscriptionList}

Respond in JSON format:
{{
  ""subscriptions"": [
    {{
      ""index"": 1,
      ""category"": ""Streaming"",
      ""insight"": ""Price increased $2 from last year"",
      ""isRealSubscription"": true
    }}
  ]
}}";

    try
    {
      var response = await _ai.GenerateTextAsync(new AiTextRequest(prompt, modelName, "subscriptions"));
      var responseText = response.Text.Trim();
      
      // Parse JSON response
      var jsonStart = responseText.IndexOf('{');
      var jsonEnd = responseText.LastIndexOf('}');
      if (jsonStart >= 0 && jsonEnd > jsonStart)
      {
        var jsonString = responseText.Substring(jsonStart, jsonEnd - jsonStart + 1);
        var aiResponse = JsonSerializer.Deserialize<AISubscriptionResponse>(jsonString, 
          new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (aiResponse?.Subscriptions != null)
        {
          foreach (var aiSub in aiResponse.Subscriptions)
          {
            if (aiSub.Index > 0 && aiSub.Index <= detectedSubscriptions.Count)
            {
              var detected = detectedSubscriptions[aiSub.Index - 1];
              detected.IsAIEnhanced = true;
              detected.DetectionMethod = "ai-enhanced";
              detected.AIInsight = aiSub.Insight;
              
              if (Enum.TryParse<SubscriptionCategory>(aiSub.Category, true, out var category))
              {
                detected.AISuggestedCategory = category;
              }
              
              // Boost or reduce confidence based on AI assessment
              if (!aiSub.IsRealSubscription)
              {
                detected.Confidence *= 0.5; // Reduce confidence if AI doubts it
                detected.IsSubscriptionLikely = detected.Confidence >= 0.5;
              }
              else
              {
                detected.Confidence = Math.Min(detected.Confidence * 1.1, 0.99); // Slight boost
              }
            }
          }
        }
      }
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to parse AI response for subscription enhancement");
    }

    return detectedSubscriptions;
  }

  // Helper class for AI response parsing
  private class AISubscriptionResponse
  {
    public List<AISubscriptionItem> Subscriptions { get; set; } = new();
  }

  private class AISubscriptionItem
  {
    public int Index { get; set; }
    public string Category { get; set; } = "";
    public string Insight { get; set; } = "";
    public bool IsRealSubscription { get; set; } = true;
  }

  public async Task<IEnumerable<DetectedSubscriptionDto>> GetPendingDetectedSubscriptionsAsync(int userId)
  {
    // Re-run detection to get current pending items
    var response = await DetectSubscriptionsAsync(userId);
    return response.DetectedSubscriptions.Where(d => d.Confidence < 0.85);
  }

  public async Task<SubscriptionDto> ConfirmDetectedSubscriptionAsync(int userId, DetectedSubscriptionDto detectedSubscription)
  {
    try
    {
      // Category precedence: an explicitly-carried category (request/AI) wins, otherwise fall
      // back to the merchant-name heuristic. Previously the heuristic always won, discarding
      // the user's/AI's chosen category.
      var category = detectedSubscription.AISuggestedCategory
        ?? SubscriptionMerchantCategories.Guess(detectedSubscription.MerchantName);

      // LastBilledDate drives the "possibly cancelled" logic. Prefer the carried last-transaction
      // date; otherwise derive it from the matched transactions so the field is never left null
      // for a detected subscription (which would make missed-cycle detection never fire).
      var lastBilledDate = detectedSubscription.LastTransactionDate
        ?? await DeriveLastBilledDateAsync(detectedSubscription.RelatedTransactionIds);

      var subscription = new Subscription
      {
        UserId = userId,
        Name = detectedSubscription.MerchantName,
        Description = $"Auto-detected subscription from transaction history",
        Amount = detectedSubscription.Amount,
        Frequency = detectedSubscription.Frequency,
        NextDueDate = detectedSubscription.EstimatedNextDue,
        LastBilledDate = lastBilledDate.HasValue
          ? DateTime.SpecifyKind(lastBilledDate.Value, DateTimeKind.Utc)
          : null,
        Category = category,
        MerchantName = detectedSubscription.MerchantName,
        Status = SubscriptionStatus.Active,
        IsDetected = true
      };

      var createdSubscription = await _subscriptionRepository.CreateAsync(subscription);

      // Link the related transactions
      foreach (var transactionId in detectedSubscription.RelatedTransactionIds)
      {
        await _subscriptionTransactionRepository.CreateAsync(new SubscriptionTransaction
        {
          SubscriptionId = createdSubscription.Id,
          TransactionId = transactionId
        });
      }

      // Close out any matching pending suggestion so confirming via the direct /subscriptions
      // /confirm path doesn't leave a ghost "Pending" row lingering in the review sheet.
      await MarkMatchingSuggestionConfirmedAsync(userId, detectedSubscription.MerchantName, createdSubscription.Id);

      _logger.LogInformation("Confirmed detected subscription {SubscriptionId} for user {UserId}",
        createdSubscription.Id, userId);

      return MapToDto(createdSubscription);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error confirming detected subscription for user {UserId}", userId);
      throw;
    }
  }

  /// <summary>
  /// Derive the most-recent transaction date from a set of transaction IDs (used to populate
  /// LastBilledDate when the caller didn't carry it). Best-effort: skips IDs that can't be loaded.
  /// </summary>
  private async Task<DateTime?> DeriveLastBilledDateAsync(IEnumerable<int> transactionIds)
  {
    DateTime? latest = null;
    foreach (var id in transactionIds)
    {
      var txn = await _transactionRepository.GetByIdAsync(id);
      if (txn != null && (latest == null || txn.Date > latest.Value))
        latest = txn.Date;
    }
    return latest;
  }

  /// <summary>
  /// Flip any still-pending SuggestedSubscription for this user+merchant to Confirmed and link it
  /// to the newly-created subscription, so no confirm path leaves a ghost pending suggestion.
  /// </summary>
  private async Task MarkMatchingSuggestionConfirmedAsync(int userId, string merchantName, int confirmedSubscriptionId)
  {
    try
    {
      var normalized = NormalizeMerchantName(merchantName);
      var suggestion = await _suggestedSubscriptionRepository.GetByMerchantAsync(userId, normalized);
      if (suggestion != null && suggestion.Status == SuggestedSubscriptionStatus.Pending)
      {
        suggestion.Status = SuggestedSubscriptionStatus.Confirmed;
        suggestion.ActionTakenAt = DateTime.UtcNow;
        suggestion.ConfirmedSubscriptionId = confirmedSubscriptionId;
        await _suggestedSubscriptionRepository.UpdateAsync(suggestion);
      }
    }
    catch (Exception ex)
    {
      // Non-fatal: the subscription is already created; a lingering suggestion is a UI nuisance,
      // not a data-loss event.
      _logger.LogWarning(ex, "Failed to mark matching suggestion confirmed for user {UserId}, merchant {Merchant}",
        userId, merchantName);
    }
  }

  public async Task DismissDetectedSubscriptionAsync(int userId, string merchantName, decimal? amount = null, string? reason = null)
  {
    try
    {
      // Normalize the merchant name before persisting so it matches the detection-time
      // comparison (IsPatternDismissedAsync / GetByMerchantName lookups all key on the
      // normalized form). Persisting the raw display name meant dismissals silently failed
      // to suppress the pattern on the next detection run.
      var normalizedMerchant = NormalizeMerchantName(merchantName);

      // Store the dismissed pattern so we don't re-suggest it
      var pattern = new DismissedSubscriptionPattern
      {
        UserId = userId,
        MerchantName = normalizedMerchant,
        DismissReason = reason
      };

      // If amount is provided, create a range around it
      if (amount.HasValue)
      {
        pattern.AmountMin = amount.Value - 3; // $3 tolerance
        pattern.AmountMax = amount.Value + 3;
      }

      await _dismissedPatternRepository.CreateAsync(pattern);
      
      _logger.LogInformation("Dismissed subscription pattern for user {UserId}: {MerchantName}", 
        userId, merchantName);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error dismissing subscription pattern for user {UserId}", userId);
      throw;
    }
  }

  public async Task<IEnumerable<CalendarSubscriptionDto>> GetCalendarDataAsync(int userId, DateTime startDate, DateTime endDate)
  {
    try
    {
      var subscriptions = await _subscriptionRepository.GetByUserIdAndDateRangeAsync(userId, startDate, endDate);
      
      var calendarData = new Dictionary<DateTime, List<SubscriptionDto>>();
      
      foreach (var subscription in subscriptions)
      {
        var date = subscription.NextDueDate.Date;
        if (!calendarData.ContainsKey(date))
        {
          calendarData[date] = new List<SubscriptionDto>();
        }
        calendarData[date].Add(MapToDto(subscription));
      }

      return calendarData.Select(kvp => new CalendarSubscriptionDto
      {
        Date = kvp.Key,
        Subscriptions = kvp.Value,
        TotalAmount = kvp.Value.Sum(s => s.Amount)
      }).OrderBy(c => c.Date);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting calendar data for user {UserId}", userId);
      throw;
    }
  }

  public async Task<SubscriptionSummaryDto> GetSubscriptionSummaryAsync(int userId)
  {
    try
    {
      var activeSubscriptions = await _subscriptionRepository.GetActiveByUserIdAsync(userId);
      
      // Sum every active subscription's monthly-equivalent amount using the same helper the
      // CategoryBreakdown below uses, so the headline total covers all frequencies (BiWeekly,
      // Quarterly, SemiAnnual, Custom, ...) and reconciles with the per-category rows.
      var totalMonthly = activeSubscriptions
        .Sum(s => ConvertToMonthlyAmount(s.Amount, s.Frequency));

      var categoryBreakdown = activeSubscriptions
        .GroupBy(s => s.Category)
        .Select(g => new SubscriptionCategorySummaryDto
        {
          Category = g.Key,
          Amount = g.Sum(s => ConvertToMonthlyAmount(s.Amount, s.Frequency)),
          Count = g.Count()
        })
        .ToList();

      return new SubscriptionSummaryDto
      {
        MonthlyTotal = totalMonthly,
        YearlyTotal = totalMonthly * 12,
        ActiveCount = activeSubscriptions.Count(),
        CategoryBreakdown = categoryBreakdown
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting subscription summary for user {UserId}", userId);
      throw;
    }
  }

  /// <summary>
  /// Remove duplicate transactions that are likely Plaid sync duplicates.
  /// Transactions with same merchant and similar amount within 2 days are considered duplicates.
  /// </summary>
  private List<Transaction> RemoveDuplicateTransactions(List<Transaction> transactions)
  {
    var result = new List<Transaction>();
    var grouped = transactions
      .Where(t => !string.IsNullOrEmpty(t.MerchantName))
      .GroupBy(t => NormalizeMerchantName(t.MerchantName));
    
    foreach (var group in grouped)
    {
      var sorted = group.OrderBy(t => t.Date).ToList();
      Transaction? lastAdded = null;
      
      foreach (var transaction in sorted)
      {
        // Check if this looks like a duplicate of the last added transaction
        if (lastAdded != null)
        {
          var daysDiff = (transaction.Date - lastAdded.Date).TotalDays;
          var amountDiff = Math.Abs(transaction.Amount - lastAdded.Amount);
          
          // Skip if within 2 days and amounts match within $1
          if (daysDiff <= 2 && amountDiff <= 1.0m)
          {
            continue; // Skip this duplicate
          }
        }
        
        result.Add(transaction);
        lastAdded = transaction;
      }
    }
    
    // Also add transactions without merchant names
    result.AddRange(transactions.Where(t => string.IsNullOrEmpty(t.MerchantName)));
    
    return result;
  }

  // Minimum real occurrences required before we assert a cadence. A 2-transaction group has a
  // single interval that is trivially "perfectly regular", so any two similar charges would look
  // like a monthly subscription. Requiring 3 gives at least 2 intervals to compare.
  private const int MinOccurrencesForCadence = 3;

  // Genuinely-annual cadence window: exactly two charges spaced ~365 days apart. Kept tight so a
  // near-year interval — not "two random purchases" — is the only thing that unlocks the
  // 2-occurrence path (which otherwise requires >=3 charges).
  private const int AnnualIntervalMinDays = 350;
  private const int AnnualIntervalMaxDays = 380;

  /// <summary>
  /// True when a cluster is exactly two charges spaced ~365 days apart — the one shape that is
  /// allowed to bypass the >=3-occurrence floor, because an annual subscription simply cannot
  /// produce 3+ charges inside the 2-year detection window. The tight day tolerance keeps two
  /// unrelated one-off purchases from qualifying (they would have to land ~a year apart).
  /// </summary>
  private static bool IsAnnualTwoChargePattern(IReadOnlyList<Transaction> cluster)
  {
    if (cluster.Count != 2) return false;
    var ordered = cluster.OrderBy(t => t.Date).ToList();
    var daysApart = (ordered[1].Date - ordered[0].Date).Days;
    return daysApart >= AnnualIntervalMinDays && daysApart <= AnnualIntervalMaxDays;
  }

  private DetectedSubscriptionDto? AnalyzeTransactionPattern(List<Transaction> transactions, SubscriptionDetectionRequest request)
  {
    // Sort transactions by date
    var sortedTransactions = transactions.OrderBy(t => t.Date).ToList();

    // CRITICAL: never assert a subscription from fewer than 3 occurrences (< 2 intervals).
    // A single interval scores as perfectly regular no matter the merchant/amount, so this
    // guard is what stops "two coffees a month apart" from becoming a monthly subscription —
    // independent of whatever MinOccurrences the caller passed. The ONLY exception is a
    // genuinely annual cadence (two charges ~365 days apart), which by definition can't reach
    // 3 charges within the detection window; the near-year interval keeps it off the
    // false-positive path the >=3 rule guards against.
    if (sortedTransactions.Count < MinOccurrencesForCadence
        && !IsAnnualTwoChargePattern(sortedTransactions))
      return null;

    // Calculate intervals between transactions
    var intervals = new List<int>();
    for (int i = 1; i < sortedTransactions.Count; i++)
    {
      var daysBetween = (sortedTransactions[i].Date - sortedTransactions[i-1].Date).Days;
      intervals.Add(daysBetween);
    }
    
    if (!intervals.Any()) return null;
    
    // Use median interval instead of average to handle occasional gaps better
    var sortedIntervals = intervals.OrderBy(i => i).ToList();
    var middleIndex = sortedIntervals.Count / 2;
    var medianInterval = sortedIntervals.Count % 2 == 0
      ? (sortedIntervals[middleIndex - 1] + sortedIntervals[middleIndex]) / 2.0
      : sortedIntervals[middleIndex];
    var avgInterval = intervals.Average();
    
    // Filter out outlier intervals (> 2x median) for variance calculation
    // This allows detecting patterns even with occasional missed months
    var normalIntervals = intervals.Where(i => i <= medianInterval * 2.5).ToList();
    var intervalVariance = normalIntervals.Any() 
      ? normalIntervals.Select(i => Math.Abs(i - medianInterval)).Average()
      : intervals.Select(i => Math.Abs(i - avgInterval)).Average();
    
    // More relaxed detection criteria - payment processing can cause delays
    // Using median for pattern detection to handle gaps
    bool isRegular = intervalVariance < 7; // Allow up to 7 days variance for real-world data
    bool isMonthly = CheckMonthlyPattern(sortedTransactions) || (medianInterval >= 28 && medianInterval <= 35);
    bool isWeekly = medianInterval >= 5 && medianInterval <= 9; // 7 days +/- 2 days
    bool isBiWeekly = medianInterval >= 12 && medianInterval <= 17; // 14 days +/- 2-3 days
    bool isQuarterly = medianInterval >= 85 && medianInterval <= 95; // ~90 days +/- 5 days
    bool isSemiAnnual = medianInterval >= 175 && medianInterval <= 195; // ~182 days +/- 10-13 days
    bool isAnnual = medianInterval >= 350 && medianInterval <= 380; // Annual +/- 15 days
    
    // Allow detection if EITHER regular OR matches a known frequency pattern
    if (!isRegular && !(isMonthly || isWeekly || isBiWeekly || isQuarterly || isSemiAnnual || isAnnual)) return null;
    
    // Determine frequency - check most specific first
    SubscriptionFrequency frequency = SubscriptionFrequency.Monthly; // Default
    if (isWeekly) frequency = SubscriptionFrequency.Weekly;
    else if (isBiWeekly) frequency = SubscriptionFrequency.BiWeekly;
    else if (isQuarterly) frequency = SubscriptionFrequency.Quarterly;
    else if (isSemiAnnual) frequency = SubscriptionFrequency.SemiAnnual;
    else if (isAnnual) frequency = SubscriptionFrequency.Annual;
    
    // Calculate next due date based on detected frequency
    var lastTransaction = sortedTransactions.Last();
    var nextDueDate = frequency switch
    {
      SubscriptionFrequency.Weekly => lastTransaction.Date.AddDays(7),
      SubscriptionFrequency.BiWeekly => lastTransaction.Date.AddDays(14),
      SubscriptionFrequency.Quarterly => lastTransaction.Date.AddMonths(3),
      SubscriptionFrequency.SemiAnnual => lastTransaction.Date.AddMonths(6),
      SubscriptionFrequency.Annual => lastTransaction.Date.AddYears(1),
      _ => lastTransaction.Date.AddMonths(1) // Monthly
    };
    
    // Calculate detailed confidence score with breakdown
    var confidenceResult = CalculateConfidenceWithBreakdown(intervals, sortedTransactions, frequency);
    
    return new DetectedSubscriptionDto
    {
      MerchantName = sortedTransactions.First().MerchantName,
      Amount = Math.Round(sortedTransactions.Average(t => Math.Abs(t.Amount)), 2),
      Frequency = frequency,
      EstimatedNextDue = nextDueDate,
      Confidence = confidenceResult.Confidence,
      ConfidenceDetails = confidenceResult.Breakdown,
      RelatedTransactionIds = sortedTransactions.Select(t => t.Id).ToList(),
      LastTransactionDate = sortedTransactions.Last().Date,
      IsSubscriptionLikely = confidenceResult.Confidence > 0.6 // Lower threshold to catch more
    };
  }

  /// <summary>
  /// Split a single merchant's transactions into amount clusters. Instead of snapping each
  /// amount to a fixed dollar bucket (which shattered price-changed subs and merged unrelated
  /// charges), we group charges that fall within a proportional tolerance of the running
  /// cluster median. Sorting by amount first keeps near-equal charges adjacent so a gradual
  /// price change (e.g. $15.99 → $17.99) stays in one cluster.
  /// </summary>
  private static List<List<Transaction>> ClusterByAmount(List<Transaction> transactions)
  {
    // Tolerance = max(25% of the cluster median, $2). Proportional keeps cheap and expensive
    // subs on equal footing; the $2 floor absorbs small utility/price-change jitter.
    const double ProportionalTolerance = 0.25;
    const decimal MinAbsoluteTolerance = 2.00m;

    var clusters = new List<List<Transaction>>();
    var sorted = transactions.OrderBy(t => Math.Abs(t.Amount)).ToList();

    foreach (var txn in sorted)
    {
      var amount = Math.Abs(txn.Amount);
      var placed = false;

      foreach (var cluster in clusters)
      {
        var median = MedianAbsAmount(cluster);
        var tolerance = Math.Max(MinAbsoluteTolerance, (decimal)ProportionalTolerance * median);
        if (Math.Abs(amount - median) <= tolerance)
        {
          cluster.Add(txn);
          placed = true;
          break;
        }
      }

      if (!placed)
        clusters.Add(new List<Transaction> { txn });
    }

    return clusters;
  }

  /// <summary>Median of the absolute transaction amounts in a cluster.</summary>
  private static decimal MedianAbsAmount(List<Transaction> transactions)
  {
    var amounts = transactions.Select(t => Math.Abs(t.Amount)).OrderBy(a => a).ToList();
    var mid = amounts.Count / 2;
    return amounts.Count % 2 == 0
      ? (amounts[mid - 1] + amounts[mid]) / 2m
      : amounts[mid];
  }

  private bool CheckMonthlyPattern(List<Transaction> sortedTransactions)
  {
    // Check if transactions occur on the same day of month (+/- 3 days for payment processing)
    var dayOfMonthVariances = new List<int>();
    
    for (int i = 1; i < sortedTransactions.Count; i++)
    {
      var prevDay = sortedTransactions[i-1].Date.Day;
      var currDay = sortedTransactions[i].Date.Day;
      
      // Handle month-end edge cases (e.g., Jan 31 -> Feb 28)
      var variance = Math.Abs(currDay - prevDay);
      if (variance > 15) // Likely month-end adjustment
      {
        // Check if it's a reasonable month-end pattern
        var prevMonth = sortedTransactions[i-1].Date.Month;
        var currMonth = sortedTransactions[i].Date.Month;
        var expectedNextMonth = prevMonth == 12 ? 1 : prevMonth + 1;
        
        if (currMonth != expectedNextMonth) continue; // Skip if not consecutive months
        
        // Adjust for month-end: if previous was near end and current is near beginning (or vice versa)
        if ((prevDay >= 28 && currDay <= 5) || (prevDay <= 5 && currDay >= 28))
          variance = 0; // Consider this a match
      }
      
      dayOfMonthVariances.Add(variance);
    }
    
    // Allow max 3-day variance for monthly patterns (was 2)
    return dayOfMonthVariances.Any() && dayOfMonthVariances.Average() <= 3.0;
  }

  /// <summary>
  /// Result of confidence calculation with detailed breakdown.
  /// </summary>
  private class ConfidenceCalculationResult
  {
    public double Confidence { get; set; }
    public ConfidenceBreakdown Breakdown { get; set; } = new();
  }

  /// <summary>
  /// Calculate confidence with detailed breakdown for transparency.
  /// Returns both the final confidence score and individual component scores.
  /// </summary>
  private ConfidenceCalculationResult CalculateConfidenceWithBreakdown(
    List<int> intervals, 
    List<Transaction> transactions,
    SubscriptionFrequency detectedFrequency)
  {
    var breakdown = new ConfidenceBreakdown();
    
    if (!intervals.Any())
    {
      return new ConfidenceCalculationResult { Confidence = 0, Breakdown = breakdown };
    }
    
    var avgInterval = intervals.Average();
    var sortedForMedian = intervals.OrderBy(i => i).ToList();
    var midIdx = sortedForMedian.Count / 2;
    var medianInterval = sortedForMedian.Count % 2 == 0
      ? (sortedForMedian[midIdx - 1] + sortedForMedian[midIdx]) / 2.0
      : sortedForMedian[midIdx];
    
    // 1. INTERVAL REGULARITY SCORE (0-1)
    // How consistently spaced are the transactions?
    var normalIntervals = intervals.Where(i => i <= medianInterval * 2.5).ToList();
    var intervalVariance = normalIntervals.Any() 
      ? normalIntervals.Select(i => Math.Abs(i - medianInterval)).Average()
      : intervals.Select(i => Math.Abs(i - avgInterval)).Average();
    
    // Scale: 0 variance = 1.0, 7+ days variance = 0.0
    breakdown.IntervalRegularityScore = Math.Max(0, Math.Min(1, 1 - (intervalVariance / 7.0)));
    
    // 2. AMOUNT CONSISTENCY SCORE (0-1)
    // How consistent are the payment amounts?
    var avgAmount = transactions.Average(t => Math.Abs(t.Amount));
    var amountVariance = transactions.Select(t => Math.Abs(Math.Abs(t.Amount) - avgAmount)).Average();
    
    // Scale: 0 variance = 1.0, $5+ variance = 0.0
    breakdown.AmountConsistencyScore = Math.Max(0, Math.Min(1, 1 - ((double)amountVariance / 5.0)));
    
    // 3. OCCURRENCE SCORE (0-1)
    // More occurrences = higher confidence
    // 2 occurrences = 0.33, 3 = 0.5, 6 = 1.0, 12+ = 1.0
    breakdown.OccurrenceScore = Math.Min(1, transactions.Count / 6.0);
    
    // 4. KNOWN MERCHANT BOOST (0-0.1)
    // Bonus if merchant matches known subscription services
    var merchantName = transactions.First().MerchantName?.ToUpperInvariant() ?? "";
    var knownSubscriptionKeywords = new[] {
      "NETFLIX", "SPOTIFY", "HULU", "DISNEY", "HBO", "APPLE", "GOOGLE", "MICROSOFT",
      "ADOBE", "AMAZON PRIME", "YOUTUBE", "GITHUB", "DROPBOX", "SLACK", "ZOOM",
      "GYM", "FITNESS", "PELOTON", "INSURANCE", "ELECTRIC", "GAS", "WATER",
      "INTERNET", "PHONE", "WIRELESS", "VERIZON", "COMCAST", "SPECTRUM",
      "ELEVENLABS", "OPENAI", "CHATGPT", "MIDJOURNEY", "SUNO"
    };
    
    if (knownSubscriptionKeywords.Any(k => merchantName.Contains(k)))
    {
      breakdown.KnownMerchantBoost = 0.1;
    }
    
    // 5. GAP PENALTY (-0.2 to 0)
    // Penalize if there are large gaps (missed billing cycles)
    var expectedInterval = detectedFrequency switch
    {
      SubscriptionFrequency.Weekly => 7,
      SubscriptionFrequency.BiWeekly => 14,
      SubscriptionFrequency.Monthly => 30,
      SubscriptionFrequency.Quarterly => 90,
      SubscriptionFrequency.SemiAnnual => 182,
      SubscriptionFrequency.Annual => 365,
      _ => 30
    };
    
    var largeGaps = intervals.Count(i => i > expectedInterval * 1.8);
    if (largeGaps > 0)
    {
      // More gaps = bigger penalty, max -0.2
      breakdown.GapPenalty = -Math.Min(0.2, largeGaps * 0.05);
    }
    
    // CALCULATE WEIGHTED FINAL SCORE
    // Base weights sum to 90%: Interval (35%), Amount (25%), Occurrences (30%)
    // Optional adjustments: KnownMerchantBoost (+10%), CrowdsourceBoost (+15% max), GapPenalty (-20% max)
    var baseScore = 
      (breakdown.IntervalRegularityScore * 0.35) +
      (breakdown.AmountConsistencyScore * 0.25) +
      (breakdown.OccurrenceScore * 0.30);
    
    var finalScore = baseScore + 
      breakdown.KnownMerchantBoost + 
      breakdown.CrowdsourceBoost + 
      breakdown.GapPenalty;
    
    // Clamp to 0.0-0.99 range
    finalScore = Math.Max(0, Math.Min(0.99, finalScore));
    
    // Generate classification and explanation
    if (finalScore >= 0.90)
    {
      breakdown.Classification = "VeryHigh";
      breakdown.Explanation = "Highly consistent pattern with regular intervals and amounts. Very likely a subscription.";
    }
    else if (finalScore >= 0.75)
    {
      breakdown.Classification = "High";
      breakdown.Explanation = "Strong pattern detected with minor variations. Probably a subscription.";
    }
    else if (finalScore >= 0.60)
    {
      breakdown.Classification = "Medium";
      breakdown.Explanation = "Pattern shows some regularity but with notable variations. Possibly a subscription - user review recommended.";
    }
    else
    {
      breakdown.Classification = "Low";
      breakdown.Explanation = "Weak pattern with irregular intervals or amounts. May be recurring spending rather than a subscription.";
    }
    
    // Add specific notes based on scores
    var notes = new List<string>();
    if (breakdown.IntervalRegularityScore < 0.5)
      notes.Add("Irregular billing intervals detected");
    if (breakdown.AmountConsistencyScore < 0.5)
      notes.Add("Payment amounts vary significantly");
    if (breakdown.OccurrenceScore < 0.5)
      notes.Add("Limited transaction history (fewer than 3 occurrences)");
    if (breakdown.GapPenalty < -0.05)
      notes.Add("Some billing cycles appear to be missed");
    if (breakdown.KnownMerchantBoost > 0)
      notes.Add("Recognized as a known subscription service");
    
    if (notes.Any())
    {
      breakdown.Explanation += " Notes: " + string.Join("; ", notes) + ".";
    }
    
    return new ConfidenceCalculationResult
    {
      Confidence = finalScore,
      Breakdown = breakdown
    };
  }

  private double CalculateConfidence(List<int> intervals, List<Transaction> transactions)
  {
    // Legacy method for backwards compatibility
    var result = CalculateConfidenceWithBreakdown(intervals, transactions, SubscriptionFrequency.Monthly);
    return result.Confidence;
  }

  private string NormalizeMerchantName(string merchantName)
  {
    if (string.IsNullOrWhiteSpace(merchantName))
      return string.Empty;
      
    var normalized = merchantName.ToUpperInvariant().Trim();
    
    // Remove common payment processor prefixes
    var prefixesToRemove = new[] { 
      "SQ *", "SQ*", "SQUARE *", "SQUARE*",  // Square
      "PP *", "PP*", "PAYPAL *", "PAYPAL*",  // PayPal
      "TST*", "TST *",                        // Toast
      "STRIPE *", "STRIPE*",                  // Stripe
      "CKO *", "CKO*",                        // Checkout.com
      "ACH DEBIT ", "ACH CREDIT ",            // ACH transfers
      "RECURRING ", "AUTOMATIC ",             // Recurring payments
      "PURCHASE ", "POS ", "DEBIT ",          // Transaction types
      "CHECKCARD ", "CHECK CARD ",
      "ONLINE PMT ", "BILL PAY ",
      "DIRECT DEBIT ",
      "WEB AUTHORIZED PMT "
    };
    
    foreach (var prefix in prefixesToRemove)
    {
      if (normalized.StartsWith(prefix))
      {
        normalized = normalized.Substring(prefix.Length);
        break; // Only one prefix should match
      }
    }
    
    // Remove common suffixes and noise
    var suffixesToRemove = new[] { 
      "*", "#", 
      "AUTOPAY", "RECURRING", "SUBSCRIPTION", "MONTHLY", "ANNUAL", "YEARLY",
      "ACH PMT", "ACH PAYMENT", "ACH DEBIT",
      "BILL PAY", "BILLPAY",
      "PAYMENT", "PMT",
      "CREDIT", "DEBIT"
    };
    
    foreach (var suffix in suffixesToRemove)
    {
      normalized = normalized.Replace(suffix, "");
    }
    
    // Remove transaction IDs and reference numbers using compiled regex (performance optimization)
    normalized = TransactionIdPattern().Replace(normalized, "");
    normalized = TrailingNumbersPattern().Replace(normalized, "");
    
    // Normalize whitespace
    normalized = WhitespacePattern().Replace(normalized, " ").Trim();
    
    return normalized;
  }

  private decimal ConvertToMonthlyAmount(decimal amount, SubscriptionFrequency frequency)
  {
    return frequency switch
    {
      SubscriptionFrequency.Weekly => amount * 4.33m,
      SubscriptionFrequency.BiWeekly => amount * 2.17m,
      SubscriptionFrequency.Monthly => amount,
      SubscriptionFrequency.Quarterly => amount / 3,
      SubscriptionFrequency.SemiAnnual => amount / 6,
      SubscriptionFrequency.Annual => amount / 12,
      _ => amount
    };
  }

  private SubscriptionDto MapToDto(Subscription subscription)
  {
    return new SubscriptionDto
    {
      Id = subscription.Id,
      Name = subscription.Name,
      Description = subscription.Description,
      Amount = subscription.Amount,
      Frequency = subscription.Frequency,
      NextDueDate = subscription.NextDueDate,
      LastBilledDate = subscription.LastBilledDate,
      Status = subscription.Status,
      Category = subscription.Category,
      IsDetected = subscription.IsDetected,
      MerchantName = subscription.MerchantName,
      CreatedAt = subscription.CreatedAt,
      UpdatedAt = subscription.UpdatedAt
    };
  }

  /// <inheritdoc />
  public async Task<bool> TriggerOnDemandDetectionAsync(int userId)
  {
    try
    {
      // Check if cooldown is active
      if (!await CanRunOnDemandDetectionAsync(userId))
      {
        _logger.LogInformation(
          "Skipping on-demand subscription detection for user {UserId} - cooldown active", 
          userId);
        return false;
      }

      _logger.LogInformation("Running on-demand subscription detection for user {UserId}", userId);

      var result = await DetectSubscriptionsAsync(userId, request: null, isOnDemand: true);
      
      if (result.TotalDetected > 0)
      {
        _logger.LogInformation(
          "On-demand detection found {Count} potential subscriptions for user {UserId}", 
          result.TotalDetected, userId);
      }
      
      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error during on-demand subscription detection for user {UserId}", userId);
      return false;
    }
  }

  /// <summary>Cooldown between on-demand subscription detections.</summary>
  private static readonly TimeSpan OnDemandDetectionCooldown = TimeSpan.FromDays(7);

  /// <inheritdoc />
  public async Task<bool> CanRunOnDemandDetectionAsync(int userId)
  {
    try
    {
      // Use the authoritative last-run timestamp on the user, not "were any suggestions created
      // recently?" — the latter reset to "allowed" whenever a run produced zero suggestions,
      // letting users re-trigger detection back-to-back.
      var user = await _userRepository.GetByIdAsync(userId);
      var lastRun = user?.LastSubscriptionDetectionRunAt;

      // Never run → allowed. Otherwise allowed once the cooldown has fully elapsed.
      return lastRun == null || lastRun.Value <= DateTime.UtcNow - OnDemandDetectionCooldown;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error checking on-demand detection cooldown for user {UserId}", userId);
      return true; // Allow on error to not block functionality
    }
  }

  /// <summary>
  /// Store detected subscriptions in the SuggestedSubscriptions table for persistence.
  /// Updates existing suggestions or creates new ones.
  /// </summary>
  private async Task StoreSuggestedSubscriptionsAsync(int userId, int? familyId, List<DetectedSubscriptionDto> detections)
  {
    try
    {
      foreach (var detection in detections)
      {
        var normalizedMerchant = NormalizeMerchantName(detection.MerchantName);
        
        // Check if we already have a suggestion for this merchant
        var existing = await _suggestedSubscriptionRepository.GetByMerchantAsync(userId, normalizedMerchant);
        
        // Look up crowdsource pattern for this merchant
        var crowdsourcePattern = await _crowdsourcedPatternRepository.GetByMerchantNameAsync(normalizedMerchant);
        
        if (existing != null)
        {
          // Update existing suggestion with new confidence/data
          existing.Amount = detection.Amount;
          existing.Frequency = detection.Frequency;
          existing.BaseConfidence = detection.Confidence;
          existing.CrowdsourceBoost = crowdsourcePattern?.ConfidenceBoost ?? 0;
          existing.Confidence = Math.Min(existing.BaseConfidence + existing.CrowdsourceBoost, 0.99);
          existing.MatchedTransactionCount = detection.RelatedTransactionIds.Count;
          existing.MatchedTransactionIds = SuggestedSubscription.SerializeTransactionIds(detection.RelatedTransactionIds);
          existing.LastTransactionDate = detection.LastTransactionDate;
          existing.PredictedNextDate = detection.EstimatedNextDue;
          existing.AISuggestedCategory = detection.AISuggestedCategory;
          existing.AIInsight = detection.AIInsight;
          existing.CrowdsourcePatternId = crowdsourcePattern?.Id;
          
          if (existing.Status == SuggestedSubscriptionStatus.Dismissed || 
              existing.Status == SuggestedSubscriptionStatus.Snoozed)
          {
            // Don't re-activate dismissed/snoozed items automatically
            continue;
          }
          
          await _suggestedSubscriptionRepository.UpdateAsync(existing);
        }
        else
        {
          // Create new suggestion
          var suggestion = new SuggestedSubscription
          {
            UserId = userId,
            FamilyId = familyId,
            NormalizedMerchantName = normalizedMerchant,
            DisplayMerchantName = detection.MerchantName,
            Amount = detection.Amount,
            Frequency = detection.Frequency,
            BaseConfidence = detection.Confidence,
            CrowdsourceBoost = crowdsourcePattern?.ConfidenceBoost ?? 0,
            Confidence = Math.Min(detection.Confidence + (crowdsourcePattern?.ConfidenceBoost ?? 0), 0.99),
            Status = SuggestedSubscriptionStatus.Pending,
            MatchedTransactionCount = detection.RelatedTransactionIds.Count,
            MatchedTransactionIds = SuggestedSubscription.SerializeTransactionIds(detection.RelatedTransactionIds),
            LastTransactionDate = detection.LastTransactionDate,
            PredictedNextDate = detection.EstimatedNextDue,
            AISuggestedCategory = detection.AISuggestedCategory,
            AIInsight = detection.AIInsight,
            CrowdsourcePatternId = crowdsourcePattern?.Id
          };

          await _suggestedSubscriptionRepository.CreateAsync(suggestion);
        }
      }
      
      _logger.LogDebug("Stored {Count} suggested subscriptions for user {UserId}", detections.Count, userId);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Error storing suggested subscriptions for user {UserId}", userId);
      // Don't fail detection if storage fails - the results are still returned
    }
  }

  /// <summary>
  /// Get timeline data for subscriptions including past transactions and future projections.
  /// </summary>
  public async Task<IEnumerable<SubscriptionTimelineDto>> GetTimelineDataAsync(int userId, SubscriptionTimelineRequest? request = null)
  {
    try
    {
      var startDate = request?.StartDate ?? DateTime.UtcNow.AddMonths(-6);
      var endDate = request?.EndDate ?? DateTime.UtcNow.AddMonths(2);
      var futureProjections = request?.FutureProjections ?? 3;

      var subscriptions = await _subscriptionRepository.GetActiveByUserIdAsync(userId);
      var result = new List<SubscriptionTimelineDto>();

      foreach (var subscription in subscriptions)
      {
        var timeline = new SubscriptionTimelineDto
        {
          SubscriptionId = subscription.Id,
          Name = subscription.Name,
          Amount = subscription.Amount,
          Frequency = subscription.Frequency,
          Category = subscription.Category,
          Status = subscription.Status,
          BillingEvents = new List<BillingEventDto>()
        };

        // Get past transactions linked to this subscription (confirmed payments)
        var subscriptionTransactions = await _subscriptionTransactionRepository.GetBySubscriptionIdAsync(subscription.Id);
        foreach (var st in subscriptionTransactions)
        {
          if (st.Transaction.Date >= startDate && st.Transaction.Date <= DateTime.UtcNow)
          {
            timeline.BillingEvents.Add(new BillingEventDto
            {
              Date = st.Transaction.Date,
              Amount = Math.Abs(st.Transaction.Amount),
              EventType = BillingEventType.Past,
              TransactionId = st.TransactionId,
              IsConfirmed = true
            });
          }
        }

        // Calculate all billing dates within range, then categorize as Past/Next/Future
        var today = DateTime.UtcNow.Date;
        var billingDate = subscription.NextDueDate;
        var foundNextDue = false;
        
        // First, go backwards from NextDueDate to find past expected billing dates
        var pastDate = billingDate;
        var pastDates = new List<DateTime>();
        while (pastDate >= startDate)
        {
          pastDates.Add(pastDate);
          pastDate = GetPreviousBillingDate(pastDate, subscription.Frequency);
        }
        
        // Add past expected billing dates (that don't already have confirmed transactions)
        var confirmedDates = timeline.BillingEvents
          .Where(e => e.IsConfirmed)
          .Select(e => e.Date.Date)
          .ToHashSet();
        
        foreach (var date in pastDates.Where(d => d.Date < today && !confirmedDates.Contains(d.Date)))
        {
          if (date >= startDate && date <= endDate)
          {
            timeline.BillingEvents.Add(new BillingEventDto
            {
              Date = date,
              Amount = subscription.Amount,
              EventType = BillingEventType.Past,
              IsConfirmed = false // Expected but not matched to a transaction
            });
          }
        }
        
        // Now calculate present and future dates
        billingDate = subscription.NextDueDate;
        
        // If NextDueDate is in the past, we need to walk forward to find the actual next due
        while (billingDate.Date < today)
        {
          billingDate = GetNextBillingDate(billingDate, subscription.Frequency);
        }
        
        // Add billing events from today forward
        var futureCount = 0;
        while (billingDate <= endDate && futureCount < futureProjections + 1)
        {
          if (!foundNextDue)
          {
            // First future/today date is "Next" (green)
            timeline.BillingEvents.Add(new BillingEventDto
            {
              Date = billingDate,
              Amount = subscription.Amount,
              EventType = BillingEventType.Next,
              IsConfirmed = false
            });
            foundNextDue = true;
          }
          else
          {
            // Subsequent dates are "Future" (purple)
            timeline.BillingEvents.Add(new BillingEventDto
            {
              Date = billingDate,
              Amount = subscription.Amount,
              EventType = BillingEventType.Future,
              IsConfirmed = false
            });
          }
          
          billingDate = GetNextBillingDate(billingDate, subscription.Frequency);
          futureCount++;
        }

        // Calculate missed billing cycles
        timeline.MissedBillingCycles = CalculateMissedBillingCycles(subscription);
        timeline.IsPotentiallyCancelled = timeline.MissedBillingCycles >= 2;

        // Sort billing events by date
        timeline.BillingEvents = timeline.BillingEvents.OrderBy(e => e.Date).ToList();
        result.Add(timeline);
      }

      return result;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting timeline data for user {UserId}", userId);
      throw;
    }
  }

  /// <summary>
  /// Get calendar billing events for the enhanced calendar view.
  /// </summary>
  public async Task<IEnumerable<CalendarBillingEventDto>> GetCalendarBillingEventsAsync(int userId, DateTime startDate, DateTime endDate)
  {
    try
    {
      var timelines = await GetTimelineDataAsync(userId, new SubscriptionTimelineRequest
      {
        StartDate = startDate,
        EndDate = endDate,
        FutureProjections = 6 // More projections for calendar view
      });

      var events = new List<CalendarBillingEventDto>();

      foreach (var timeline in timelines)
      {
        foreach (var billingEvent in timeline.BillingEvents)
        {
          if (billingEvent.Date >= startDate && billingEvent.Date <= endDate)
          {
            events.Add(new CalendarBillingEventDto
            {
              Date = billingEvent.Date.Date, // Normalize to just date
              SubscriptionId = timeline.SubscriptionId,
              SubscriptionName = timeline.Name,
              Amount = billingEvent.Amount,
              EventType = billingEvent.EventType,
              IsConfirmed = billingEvent.IsConfirmed,
              TransactionId = billingEvent.TransactionId
            });
          }
        }
      }

      return events.OrderBy(e => e.Date).ThenBy(e => e.SubscriptionName);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting calendar billing events for user {UserId}", userId);
      throw;
    }
  }

  /// <summary>
  /// Get subscriptions that appear to be cancelled.
  /// </summary>
  public async Task<IEnumerable<SubscriptionTimelineDto>> GetPotentiallyCancelledSubscriptionsAsync(int userId)
  {
    try
    {
      var timelines = await GetTimelineDataAsync(userId);
      return timelines.Where(t => t.IsPotentiallyCancelled);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting potentially cancelled subscriptions for user {UserId}", userId);
      throw;
    }
  }

  /// <summary>
  /// Calculate the next billing date based on frequency.
  /// </summary>
  private DateTime GetNextBillingDate(DateTime currentDate, SubscriptionFrequency frequency)
  {
    return frequency switch
    {
      SubscriptionFrequency.Weekly => currentDate.AddDays(7),
      SubscriptionFrequency.BiWeekly => currentDate.AddDays(14),
      SubscriptionFrequency.Monthly => currentDate.AddMonths(1),
      SubscriptionFrequency.Quarterly => currentDate.AddMonths(3),
      SubscriptionFrequency.SemiAnnual => currentDate.AddMonths(6),
      SubscriptionFrequency.Annual => currentDate.AddYears(1),
      _ => currentDate.AddMonths(1) // Default to monthly
    };
  }

  /// <summary>
  /// Calculate the previous billing date based on frequency.
  /// </summary>
  private DateTime GetPreviousBillingDate(DateTime currentDate, SubscriptionFrequency frequency)
  {
    return frequency switch
    {
      SubscriptionFrequency.Weekly => currentDate.AddDays(-7),
      SubscriptionFrequency.BiWeekly => currentDate.AddDays(-14),
      SubscriptionFrequency.Monthly => currentDate.AddMonths(-1),
      SubscriptionFrequency.Quarterly => currentDate.AddMonths(-3),
      SubscriptionFrequency.SemiAnnual => currentDate.AddMonths(-6),
      SubscriptionFrequency.Annual => currentDate.AddYears(-1),
      _ => currentDate.AddMonths(-1) // Default to monthly
    };
  }

  /// <summary>
  /// Calculate the number of billing cycles that have been missed based on LastBilledDate.
  /// </summary>
  private int CalculateMissedBillingCycles(Subscription subscription)
  {
    if (subscription.LastBilledDate == null)
      return 0;

    var lastBilled = subscription.LastBilledDate.Value;
    var now = DateTime.UtcNow;
    var expectedDate = GetNextBillingDate(lastBilled, subscription.Frequency);
    var missedCycles = 0;

    while (expectedDate < now)
    {
      missedCycles++;
      expectedDate = GetNextBillingDate(expectedDate, subscription.Frequency);
    }

    return missedCycles;
  }
}
