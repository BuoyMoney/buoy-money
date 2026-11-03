using System.ComponentModel;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tools for reading user preferences and settings.
/// </summary>
[McpServerToolType]
public sealed class UserPreferencesTools
{
    private readonly IUserRepository _userRepository;
    private readonly ILogger<UserPreferencesTools> _logger;

    public UserPreferencesTools(
        IUserRepository userRepository,
        ILogger<UserPreferencesTools> logger)
    {
        _userRepository = userRepository;
        _logger = logger;
    }

    [McpServerTool(Name = "get_user_preferences", ReadOnly = true)]
    [Description("Get the user's AI interaction preferences. This determines how the AI should interact - Quick mode uses sensible defaults, Clarifying mode asks more questions before acting.")]
    public async Task<string> GetUserPreferencesAsync(
        [Description("User ID to get preferences for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_user_preferences called for user {UserId}", userId);
            
            var user = await _userRepository.GetByIdAsync(userId);
            
            if (user == null)
            {
                return "User not found.";
            }

            var result = new
            {
                AIInteractionMode = user.AiInteractionMode.ToString(),
                ModeDescription = user.AiInteractionMode == AIInteractionMode.Quick
                    ? "Quick mode: Use sensible defaults for budget settings (rollover=true, countUp=true), but always confirm before creating or modifying data."
                    : "Clarifying mode: Ask questions about ambiguous settings (like rollover preference) AND confirm before creating or modifying data.",
                Defaults = new
                {
                    BudgetRollover = true,
                    BudgetCountUp = true,
                    Description = "When creating budgets in Quick mode, enable both rollover (unspent money carries over) and count-up (daily allowances accumulate) by default."
                },
                Guidance = new
                {
                    WriteOperations = "Always show a confirmation UI before executing write operations (create, update, delete).",
                    ClarifyingQuestions = user.AiInteractionMode == AIInteractionMode.Clarifying
                        ? "Ask the user about optional settings before showing confirmation."
                        : "Skip optional questions and use defaults, but still confirm the final action."
                }
            };

            return System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_user_preferences for user {UserId}", userId);
            return $"Error retrieving user preferences: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_user_context", ReadOnly = true)]
    [Description("Get general context about the user including subscription tier, family membership, and account status. Use this to understand what features are available to the user.")]
    public async Task<string> GetUserContextAsync(
        [Description("User ID to get context for")] int userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_user_context called for user {UserId}", userId);
            
            var user = await _userRepository.GetByIdAsync(userId);
            
            if (user == null)
            {
                return "User not found.";
            }

            var result = new
            {
                UserId = user.Id,
                FirstName = user.FirstName,
                Currency = user.Currency,
                Subscription = new
                {
                    Tier = user.SubscriptionTier?.ToString() ?? "None",
                    IsPremium = user.SubscriptionTier == SubscriptionTier.Premium || 
                                user.SubscriptionTier == SubscriptionTier.PremiumInherited ||
                                user.SubscriptionTier == SubscriptionTier.Admin,
                    IsTrialActive = user.IsTrialActive,
                    TrialEndsAt = user.TrialEndDate?.ToString("yyyy-MM-dd")
                },
                Family = new
                {
                    HasFamily = user.FamilyId.HasValue,
                    FamilyId = user.FamilyId
                },
                AccountStatus = new
                {
                    CreatedAt = user.CreatedAt.ToString("yyyy-MM-dd"),
                    HasMfaEnabled = user.HasMfaEnabled
                }
            };

            return System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_user_context for user {UserId}", userId);
            return $"Error retrieving user context: {ex.Message}";
        }
    }
}
