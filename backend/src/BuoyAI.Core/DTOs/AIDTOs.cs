using BuoyAI.Core.Enums;

namespace BuoyAI.Core.DTOs.AI;

public class AIQueryRequest
{
  public string Query { get; set; } = string.Empty;
  public AIQueryContext? Context { get; set; }

  /// <summary>Set by the controller from the X-Client-Type header (see AIChatRequest.ClientType).</summary>
  public string? ClientType { get; set; }
  
  /// <summary>
  /// Conversation history for multi-turn chat context
  /// </summary>
  public List<ConversationMessage>? ConversationHistory { get; set; }
}

/// <summary>
/// A message in the conversation history
/// </summary>
public class ConversationMessage
{
  public string Role { get; set; } = string.Empty; // "user" or "assistant"
  public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Request to execute an MCP tool directly
/// </summary>
public class ExecuteToolRequest
{
  public string ToolName { get; set; } = string.Empty;
  public Dictionary<string, object>? Arguments { get; set; }
  
  /// <summary>
  /// Confirmation token for write operations.
  /// If provided, validates and consumes the token before execution.
  /// </summary>
  public Guid? ConfirmationToken { get; set; }
}

/// <summary>
/// Response when a write operation requires confirmation
/// </summary>
public class ToolConfirmationResponse
{
  public bool RequiresConfirmation { get; set; } = true;
  public Guid ConfirmationToken { get; set; }
  public string ToolName { get; set; } = string.Empty;
  public Dictionary<string, object>? Arguments { get; set; }
  public string Preview { get; set; } = string.Empty;
  public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Request to update AI preferences
/// </summary>
public class UpdateAIPreferencesRequest
{
  public string AiInteractionMode { get; set; } = string.Empty;
}

public class AIQueryContext
{
  public DateTime CurrentDate { get; set; } = DateTime.UtcNow;
  public int UserId { get; set; }
  public string? RequestContext { get; set; }
  
  // Enhanced financial summary
  public FinancialSummary? FinancialSummary { get; set; }
  
  // Legacy properties for backward compatibility
  public decimal IncomeThisMonth { get; set; }
  public Dictionary<string, decimal> RemainingBudget { get; set; } = new();
  public List<object> RecentTransactions { get; set; } = new();
}

public class FinancialSummary
{
  public decimal TotalBalance { get; set; }
  public decimal MonthlyIncome { get; set; }
  public decimal MonthlySpending { get; set; }
  public decimal NetCashFlow => MonthlyIncome - MonthlySpending;
  public decimal SavingsRate => MonthlyIncome > 0 ? (NetCashFlow / MonthlyIncome) * 100 : 0;
  
  public int TransactionCount { get; set; }
  public int AccountCount { get; set; }
  
  public List<CategorySpending> TopSpendingCategories { get; set; } = new();
  public List<BudgetPerformance> BudgetPerformance { get; set; } = new();
  public List<RecentTransactionSummary> RecentTransactions { get; set; } = new();
  
  public DateTime AnalysisPeriodStart { get; set; }
  public DateTime AnalysisPeriodEnd { get; set; }
}

public class CategorySpending
{
  public string Category { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public int TransactionCount { get; set; }
  public decimal PercentageOfTotal { get; set; }
}

public class BudgetPerformance
{
  public string Category { get; set; } = string.Empty;
  public decimal Allocated { get; set; }
  public decimal Spent { get; set; }
  public decimal Remaining => Allocated - Spent;
  public decimal PercentageUsed => Allocated > 0 ? (Spent / Allocated) * 100 : 0;
  public bool IsOverBudget => Spent > Allocated;
}

public class RecentTransactionSummary
{
  public DateTime Date { get; set; }
  public string Description { get; set; } = string.Empty;
  public string Category { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public string Type { get; set; } = string.Empty; // Credit/Debit
}

public class AIQueryResponse
{
  public string Response { get; set; } = string.Empty;
  public Dictionary<string, object>? Data { get; set; }
  public bool Success { get; set; } = true;
  public string? Error { get; set; }
  
  /// <summary>
  /// Results from any MCP tool calls made during this query
  /// </summary>
  public List<ToolCallResult>? ToolResults { get; set; }
}

/// <summary>
/// Represents the result of an MCP tool call
/// </summary>
public class ToolCallResult
{
  public string ToolName { get; set; } = string.Empty;
  public Dictionary<string, object>? Arguments { get; set; }
  public string Result { get; set; } = string.Empty;
  
  /// <summary>
  /// True if this is a write operation that requires user confirmation
  /// </summary>
  public bool RequiresConfirmation { get; set; }

  /// <summary>
  /// One-time token issued with a preview. The Confirm tap sends it back to
  /// POST /api/ai/tools/execute, which runs the stored tool and arguments (what was previewed).
  /// </summary>
  public Guid? ConfirmationToken { get; set; }
}

/// <summary>
/// A structured, one-tap follow-up the app can offer for an insight. Two kinds:
///  - "navigate": take the user to a page to look at / fix something themselves (safe, no writes).
///  - "agent_handoff": hand a natural-language <see cref="Instruction"/> to a write-capable
///    follow-up agent. The user pressing the button IS the confirmation for those writes, and an
///    undo is offered afterward (see the insights act/undo endpoints).
/// This is generated by the insight model and persisted inside AIInsight.Data (no schema change).
/// </summary>
public class SuggestedAction
{
  public const string TypeNavigate = "navigate";
  public const string TypeAgentHandoff = "agent_handoff";

  /// <summary>"navigate" or "agent_handoff".</summary>
  public string Type { get; set; } = TypeNavigate;

  /// <summary>Button label shown to the user, e.g. "Review these transactions".</summary>
  public string Label { get; set; } = string.Empty;

  /// <summary>For "navigate": the in-app route, e.g. "/transactions".</summary>
  public string? Route { get; set; }

  /// <summary>
  /// For "agent_handoff": the natural-language task the write-capable follow-up agent should
  /// carry out (e.g. "Recategorize the 3 unreviewed Robinhood transactions as Transfer").
  /// </summary>
  public string? Instruction { get; set; }

  /// <summary>
  /// For "agent_handoff": a short, plain-English description of what pressing the button will do,
  /// shown to the user so the one-tap confirmation is informed.
  /// </summary>
  public string? Summary { get; set; }
}

// Enhanced DTOs for AI Insights feature
public class AIInsightDto
{
  public string Id { get; set; } = string.Empty;
  public string Type { get; set; } = string.Empty; // 'spending', 'budget', 'savings', 'prediction', 'recommendation'
  public string Title { get; set; } = string.Empty;
  public string Message { get; set; } = string.Empty;
  public string? ActionSuggestion { get; set; }
  public Dictionary<string, object>? Data { get; set; }

  /// <summary>Optional structured follow-up (navigate or agent handoff). Persisted in AIInsight.Data.</summary>
  public SuggestedAction? SuggestedAction { get; set; }

  public double Confidence { get; set; }
  public DateTime GeneratedAt { get; set; }
  public string Priority { get; set; } = "medium"; // 'low', 'medium', 'high'
  public bool IsDismissed { get; set; } = false;

  /// <summary>
  /// Internal-only. Archived insights are already filtered out server-side
  /// (AIInsightRepository) so this never reaches the client as anything but false;
  /// no Flutter model reads it. Kept for entity↔DTO mapping but not serialized to the wire.
  /// </summary>
  [System.Text.Json.Serialization.JsonIgnore]
  public bool IsArchived { get; set; } = false;
}

public class AIInsightRequest
{
  public string Context { get; set; } = string.Empty; // 'dashboard', 'budget', 'savings', etc.
  public string? SpecificType { get; set; } // Optional filter for specific insight type
  public int MaxInsights { get; set; } = 5;
}

/// <summary>Request to run an insight's "agent_handoff" SuggestedAction. The button press IS the confirmation.</summary>
public class InsightActRequest
{
  /// <summary>The natural-language task from the SuggestedAction.Instruction.</summary>
  public string Instruction { get; set; } = string.Empty;

  /// <summary>Optional insight id, for audit/logging only.</summary>
  public string? InsightId { get; set; }
}

/// <summary>One change the follow-up agent made, described for the user.</summary>
public class InsightActionChange
{
  public string Description { get; set; } = string.Empty;
}

public class InsightActResponse
{
  public bool Success { get; set; }
  public string Summary { get; set; } = string.Empty;
  public List<InsightActionChange> Changes { get; set; } = new();

  /// <summary>Signed, stateless journal of inverse operations. Null when nothing reversible happened.</summary>
  public string? UndoToken { get; set; }
  public bool CanUndo => !string.IsNullOrEmpty(UndoToken);
}

public class InsightUndoRequest
{
  public string UndoToken { get; set; } = string.Empty;
}

public class InsightUndoResponse
{
  public bool Success { get; set; }
  public string Summary { get; set; } = string.Empty;
}

public class AIUsageStats
{
  public int RequestsToday { get; set; }
  public int RequestsThisMonth { get; set; }
  public int DailyLimit { get; set; }
  public int MonthlyLimit { get; set; }
  public decimal CostToday { get; set; }
  public decimal CostThisMonth { get; set; }
  public decimal MonthlyBudget { get; set; }
  public decimal UsagePercent { get; set; }
  public string? WarningLevel { get; set; } // null, "warning" (80%), or "blocked" (100%)
  public string SubscriptionTier { get; set; } = string.Empty;
  public DateTime LastRequestAt { get; set; }
  
  // Refresh tracking
  public Dictionary<string, RefreshStats> RefreshStats { get; set; } = new();
}

public class RefreshStats
{
  public int UsedToday { get; set; }
  public int DailyLimit { get; set; }
  public bool CanRefresh => UsedToday < DailyLimit;
  public DateTime? LastRefreshAt { get; set; }
}

public class DismissInsightRequest
{
  public string InsightId { get; set; } = string.Empty;
}

public class AIContextData
{
  public int UserId { get; set; }
  public SubscriptionTier SubscriptionTier { get; set; }
  public string RequestContext { get; set; } = string.Empty; // 'dashboard', 'budget', 'savings', etc.
  public Dictionary<string, object> FinancialData { get; set; } = new();
  public List<string> RecentInsightIds { get; set; } = new(); // Avoid duplicates
}

/// <summary>
/// Request for AI chat - Pro feature for discussing financial picture
/// </summary>
public class AIChatRequest
{
  public string Message { get; set; } = string.Empty;
  
  /// <summary>
  /// Session ID for conversation persistence. If null, a new conversation will be created.
  /// </summary>
  public Guid? SessionId { get; set; }
  
  [Obsolete("Use SessionId instead")]
  public string? ConversationId { get; set; }
  
  public AIQueryContext? Context { get; set; }
  
  /// <summary>
  /// Conversation history for the current session. If SessionId is provided,
  /// history will be loaded from database and this field will be ignored.
  /// </summary>
  public List<ChatMessage>? ConversationHistory { get; set; }
  
  /// <summary>
  /// Optional file references to include with this message.
  /// Files should be uploaded first via /api/ai/chat/upload endpoint.
  /// </summary>
  public List<ChatFileReference>? Files { get; set; }

  /// <summary>
  /// Where the answer will be read ("web", "mobile-ios", "mobile-android", ...). Set by the
  /// controller from the X-Client-Type header, never trusted from the body; a phone gets
  /// answers formatted without tables.
  /// </summary>
  public string? ClientType { get; set; }
}

public class ChatMessage
{
  public string Role { get; set; } = "user"; // 'user' or 'assistant'
  public string Content { get; set; } = string.Empty;
  public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Reference to an uploaded file for AI chat
/// </summary>
public class ChatFileReference
{
  /// <summary>
  /// Gemini Files API URI (e.g., "https://generativelanguage.googleapis.com/v1beta/files/abc123")
  /// </summary>
  public string FileUri { get; set; } = string.Empty;
  
  /// <summary>
  /// MIME type of the uploaded file
  /// </summary>
  public string MimeType { get; set; } = string.Empty;
  
  /// <summary>
  /// Original filename for display purposes
  /// </summary>
  public string DisplayName { get; set; } = string.Empty;
  
  /// <summary>
  /// File size in bytes
  /// </summary>
  public long SizeBytes { get; set; }
}

/// <summary>
/// Response from file upload endpoint
/// </summary>
public class ChatFileUploadResponse
{
  public bool Success { get; set; }
  public string? FileUri { get; set; }
  public string? MimeType { get; set; }
  public string? DisplayName { get; set; }
  public long SizeBytes { get; set; }
  public string? Error { get; set; }
  
  /// <summary>
  /// Expiration time (Gemini files expire after 48 hours)
  /// </summary>
  public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Allowed file types for AI chat upload
/// </summary>
public static class AllowedChatFileTypes
{
  public static readonly Dictionary<string, string> MimeTypes = new()
  {
    // Bank export formats (from Issue #311)
    { ".ofx", "application/x-ofx" },
    { ".qfx", "application/vnd.intu.qfx" },
    { ".qif", "application/qif" },
    
    // Data formats
    { ".csv", "text/csv" },
    { ".txt", "text/plain" },
    
    // Document formats
    { ".pdf", "application/pdf" }
  };
  
  /// <summary>
  /// Maximum file size per type in bytes
  /// </summary>
  public static readonly Dictionary<string, long> MaxSizeBytes = new()
  {
    { "application/pdf", 10 * 1024 * 1024 },      // 10MB for PDFs
    { "application/x-ofx", 5 * 1024 * 1024 },     // 5MB for bank files
    { "application/vnd.intu.qfx", 5 * 1024 * 1024 },
    { "application/qif", 5 * 1024 * 1024 },
    { "text/csv", 5 * 1024 * 1024 },              // 5MB for CSV
    { "text/plain", 2 * 1024 * 1024 }             // 2MB for text
  };
  
  public static bool IsAllowed(string mimeType) => MaxSizeBytes.ContainsKey(mimeType);
  
  public static long GetMaxSize(string mimeType) => 
    MaxSizeBytes.TryGetValue(mimeType, out var size) ? size : 0;
  
  public static string? GetMimeTypeFromExtension(string extension) =>
    MimeTypes.TryGetValue(extension.ToLowerInvariant(), out var mimeType) ? mimeType : null;
}

public class AIChatResponse
{
  public bool Success { get; set; } = true;
  public string Message { get; set; } = string.Empty;
  public string? Error { get; set; }
  public string? ConversationId { get; set; }
  public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Server-Sent Event for AI chat streaming
/// </summary>
public class ChatStreamEvent
{
  /// <summary>
  /// Event type: text, function_call, function_result, confirmation_required, done, error
  /// </summary>
  public string Type { get; set; } = string.Empty;
  
  /// <summary>
  /// Text content (for text events)
  /// </summary>
  public string? Text { get; set; }
  
  /// <summary>
  /// Function name being called (for function_call events)
  /// </summary>
  public string? FunctionName { get; set; }
  
  /// <summary>
  /// Function arguments (for function_call events)
  /// </summary>
  public Dictionary<string, object>? Arguments { get; set; }
  
  /// <summary>
  /// Function result (for function_result events)
  /// </summary>
  public string? FunctionResult { get; set; }
  
  /// <summary>
  /// Tool result details for confirmations
  /// </summary>
  public ToolCallResult? ToolResult { get; set; }
  
  /// <summary>
  /// Error message (for error events)
  /// </summary>
  public string? Error { get; set; }
  
  /// <summary>
  /// Session ID for the conversation (returned in done event)
  /// </summary>
  public Guid? SessionId { get; set; }
  
  /// <summary>
  /// Conversation ID (database ID) for the conversation (returned in done event)
  /// </summary>
  public int? ConversationId { get; set; }
}
