using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.DTOs.Subscription;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tools for creating and managing subscriptions.
/// Allows AI to help users add detected subscriptions with user confirmation.
/// </summary>
[McpServerToolType]
public sealed class SubscriptionWriteTools
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<SubscriptionWriteTools> _logger;

    public SubscriptionWriteTools(
        ISubscriptionService subscriptionService,
        IUserRepository userRepository,
        ILogger<SubscriptionWriteTools> logger)
    {
        _subscriptionService = subscriptionService;
        _userRepository = userRepository;
        _logger = logger;
    }

    [McpServerTool(Name = "add_subscription")]
    [Description("Add a new subscription to track for the user. Use this when the user wants to track a recurring bill or service. Returns a confirmation request showing what will be added.")]
    public async Task<string> AddSubscriptionAsync(
        [Description("User ID to add subscription for")] int userId,
        [Description("Name of the subscription (e.g., 'Netflix', 'Gym Membership')")] string name,
        [Description("Monthly or billing amount in dollars")] decimal amount,
        [Description("Billing frequency: Weekly, BiWeekly, Monthly, Quarterly, SemiAnnual, Annual")] string frequency,
        [Description("Next due date (format: YYYY-MM-DD)")] string nextDueDate,
        [Description("Category: Streaming, Software, Utilities, Fitness, News, Gaming, Food, Shopping, Insurance, Other")] string? category = null,
        [Description("Merchant name as it appears on bank statements")] string? merchantName = null,
        [Description("Description or notes about this subscription")] string? description = null,
        [Description("Set to true to execute the action after user confirmation")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: add_subscription called for user {UserId}, name={Name}, amount={Amount}, confirmed={Confirmed}", 
                userId, name, amount, confirmed);

            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                return JsonSerializer.Serialize(new { Error = "User not found" });
            }

            // Parse frequency
            if (!Enum.TryParse<SubscriptionFrequency>(frequency, true, out var parsedFrequency))
            {
                return JsonSerializer.Serialize(new { 
                    Error = $"Invalid frequency: {frequency}. Use one of: Weekly, BiWeekly, Monthly, Quarterly, SemiAnnual, Annual" 
                });
            }

            // Parse next due date
            if (!DateTime.TryParse(nextDueDate, out var parsedNextDue))
            {
                return JsonSerializer.Serialize(new { 
                    Error = $"Invalid date format: {nextDueDate}. Please use YYYY-MM-DD format." 
                });
            }

            // Parse category (default to Other)
            var parsedCategory = SubscriptionCategory.Other;
            if (!string.IsNullOrEmpty(category))
            {
                Enum.TryParse<SubscriptionCategory>(category, true, out parsedCategory);
            }

            // Check for existing subscription with same name or merchant
            var existingSubscriptions = await _subscriptionService.GetSubscriptionsAsync(userId);
            var duplicateName = existingSubscriptions.FirstOrDefault(s => 
                s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var duplicateMerchant = merchantName != null ? existingSubscriptions.FirstOrDefault(s => 
                s.MerchantName?.Equals(merchantName, StringComparison.OrdinalIgnoreCase) == true) : null;

            if (duplicateName != null || duplicateMerchant != null)
            {
                var existing = duplicateName ?? duplicateMerchant!;
                return JsonSerializer.Serialize(new
                {
                    RequiresAction = true,
                    ActionType = "subscription_exists",
                    Message = $"A similar subscription '{existing.Name}' already exists ({CurrencyCodes.Format(existing.Amount, user.Currency)}/{existing.Frequency}).",
                    ExistingSubscription = new
                    {
                        existing.Id,
                        existing.Name,
                        existing.Amount,
                        Frequency = existing.Frequency.ToString(),
                        existing.MerchantName
                    },
                    SuggestedAction = "Would you like to update the existing subscription or add a new one with a different name?"
                });
            }

            // Calculate monthly equivalent for display
            var monthlyEquivalent = parsedFrequency switch
            {
                SubscriptionFrequency.Weekly => amount * 4.33m,
                SubscriptionFrequency.BiWeekly => amount * 2.17m,
                SubscriptionFrequency.Monthly => amount,
                SubscriptionFrequency.Quarterly => amount / 3m,
                SubscriptionFrequency.SemiAnnual => amount / 6m,
                SubscriptionFrequency.Annual => amount / 12m,
                _ => amount
            };

            if (!confirmed)
            {
                return JsonSerializer.Serialize(new
                {
                    RequiresConfirmation = true,
                    ActionType = "add_subscription",
                    Message = $"Ready to add '{name}' subscription",
                    Details = new
                    {
                        Name = name,
                        Amount = amount,
                        Frequency = parsedFrequency.ToString(),
                        MonthlyEquivalent = Math.Round(monthlyEquivalent, 2),
                        YearlyTotal = Math.Round(monthlyEquivalent * 12, 2),
                        NextDueDate = parsedNextDue.ToString("yyyy-MM-dd"),
                        Category = parsedCategory.ToString(),
                        MerchantName = merchantName,
                        Description = description
                    },
                    ConfirmationPrompt = $"Add {name} at {CurrencyCodes.Format(amount, user.Currency)}/{parsedFrequency} (≈{CurrencyCodes.Format(monthlyEquivalent, user.Currency)}/month)?",
                    CallbackData = new
                    {
                        ToolName = "add_subscription",
                        UserId = userId,
                        Name = name,
                        Amount = amount,
                        Frequency = frequency,
                        NextDueDate = nextDueDate,
                        Category = category,
                        MerchantName = merchantName,
                        Description = description,
                        Confirmed = true
                    }
                });
            }

            // Create the subscription
            var request = new CreateSubscriptionRequest
            {
                Name = name,
                Amount = amount,
                Frequency = parsedFrequency,
                NextDueDate = parsedNextDue,
                Category = parsedCategory,
                MerchantName = merchantName,
                Description = description ?? $"Added via AI assistant"
            };

            var created = await _subscriptionService.CreateSubscriptionAsync(userId, request);

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Successfully added '{name}' subscription!",
                Subscription = new
                {
                    created.Id,
                    created.Name,
                    created.Amount,
                    Frequency = created.Frequency.ToString(),
                    NextDueDate = created.NextDueDate.ToString("yyyy-MM-dd"),
                    Category = created.Category.ToString(),
                    MonthlyEquivalent = Math.Round(monthlyEquivalent, 2)
                },
                NextSteps = new[]
                {
                    "View all subscriptions on the Subscriptions page",
                    "Set up notifications for upcoming payments",
                    "Review if there are other subscriptions to add"
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in add_subscription for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "confirm_detected_subscription")]
    [Description("Confirm a detected subscription pattern and add it to tracking. Use this when the user agrees that a detected recurring payment is indeed a subscription they want to track.")]
    public async Task<string> ConfirmDetectedSubscriptionAsync(
        [Description("User ID to confirm subscription for")] int userId,
        [Description("Merchant name of the detected subscription")] string merchantName,
        [Description("Amount of the subscription")] decimal amount,
        [Description("Detected frequency: Weekly, BiWeekly, Monthly, Quarterly, SemiAnnual, Annual")] string frequency,
        [Description("Estimated next due date (format: YYYY-MM-DD)")] string nextDueDate,
        [Description("Set to true to execute the action after user confirmation")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: confirm_detected_subscription called for user {UserId}, merchant={Merchant}, confirmed={Confirmed}", 
                userId, merchantName, confirmed);

            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                return JsonSerializer.Serialize(new { Error = "User not found" });
            }

            // Parse frequency
            if (!Enum.TryParse<SubscriptionFrequency>(frequency, true, out var parsedFrequency))
            {
                return JsonSerializer.Serialize(new { 
                    Error = $"Invalid frequency: {frequency}. Use one of: Weekly, BiWeekly, Monthly, Quarterly, SemiAnnual, Annual" 
                });
            }

            // Parse next due date
            if (!DateTime.TryParse(nextDueDate, out var parsedNextDue))
            {
                return JsonSerializer.Serialize(new { 
                    Error = $"Invalid date format: {nextDueDate}. Please use YYYY-MM-DD format." 
                });
            }

            if (!confirmed)
            {
                // Calculate monthly equivalent
                var monthlyEquivalent = parsedFrequency switch
                {
                    SubscriptionFrequency.Weekly => amount * 4.33m,
                    SubscriptionFrequency.BiWeekly => amount * 2.17m,
                    SubscriptionFrequency.Monthly => amount,
                    SubscriptionFrequency.Quarterly => amount / 3m,
                    SubscriptionFrequency.SemiAnnual => amount / 6m,
                    SubscriptionFrequency.Annual => amount / 12m,
                    _ => amount
                };

                return JsonSerializer.Serialize(new
                {
                    RequiresConfirmation = true,
                    ActionType = "confirm_subscription",
                    Message = $"Found '{merchantName}' in your transactions",
                    Details = new
                    {
                        MerchantName = merchantName,
                        Amount = amount,
                        Frequency = parsedFrequency.ToString(),
                        MonthlyEquivalent = Math.Round(monthlyEquivalent, 2),
                        NextDueDate = parsedNextDue.ToString("yyyy-MM-dd")
                    },
                    ConfirmationPrompt = $"Add {merchantName} ({CurrencyCodes.Format(amount, user.Currency)}/{parsedFrequency}) to your tracked subscriptions?",
                    CallbackData = new
                    {
                        ToolName = "confirm_detected_subscription",
                        UserId = userId,
                        MerchantName = merchantName,
                        Amount = amount,
                        Frequency = frequency,
                        NextDueDate = nextDueDate,
                        Confirmed = true
                    }
                });
            }

            // Create the DetectedSubscriptionDto for confirmation
            var detected = new DetectedSubscriptionDto
            {
                MerchantName = merchantName,
                Amount = amount,
                Frequency = parsedFrequency,
                EstimatedNextDue = parsedNextDue,
                Confidence = 0.9, // High confidence since user is confirming
                RelatedTransactionIds = new List<int>(),
                IsSubscriptionLikely = true
            };

            var created = await _subscriptionService.ConfirmDetectedSubscriptionAsync(userId, detected);

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Now tracking '{merchantName}' subscription!",
                Subscription = new
                {
                    created.Id,
                    created.Name,
                    created.Amount,
                    Frequency = created.Frequency.ToString(),
                    NextDueDate = created.NextDueDate.ToString("yyyy-MM-dd"),
                    Category = created.Category.ToString()
                },
                Tips = new[]
                {
                    $"You'll be reminded before the next payment on {parsedNextDue:MMM d}",
                    "Check for more subscriptions with 'Do I have other subscriptions?'"
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in confirm_detected_subscription for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "dismiss_subscription_suggestion")]
    [Description("Dismiss a detected subscription pattern so it won't be suggested again. Use when the user says a detected pattern is not actually a subscription.")]
    public async Task<string> DismissSubscriptionSuggestionAsync(
        [Description("User ID to dismiss suggestion for")] int userId,
        [Description("Merchant name of the subscription to dismiss")] string merchantName,
        [Description("Amount to dismiss (optional, helps identify specific pattern)")] decimal? amount = null,
        [Description("Reason for dismissal (e.g., 'One-time purchase', 'Not a subscription')")] string? reason = null,
        [Description("Set to true to execute the action")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: dismiss_subscription_suggestion called for user {UserId}, merchant={Merchant}", 
                userId, merchantName);

            if (!confirmed)
            {
                return JsonSerializer.Serialize(new
                {
                    RequiresConfirmation = true,
                    ActionType = "dismiss_suggestion",
                    Message = $"Dismiss '{merchantName}' from subscription suggestions?",
                    Details = new
                    {
                        MerchantName = merchantName,
                        Amount = amount,
                        Reason = reason ?? "Not a subscription"
                    },
                    Note = "This pattern won't be suggested again for 60 days.",
                    CallbackData = new
                    {
                        ToolName = "dismiss_subscription_suggestion",
                        UserId = userId,
                        MerchantName = merchantName,
                        Amount = amount,
                        Reason = reason,
                        Confirmed = true
                    }
                });
            }

            await _subscriptionService.DismissDetectedSubscriptionAsync(userId, merchantName, amount, reason);

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"'{merchantName}' has been dismissed from suggestions.",
                Note = "It won't appear in subscription detection for 60 days."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in dismiss_subscription_suggestion for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }
}
