namespace BuoyAI.Core.DTOs.AI;

// ============================================
// AI Conversation DTOs
// ============================================

/// <summary>
/// DTO for an AI conversation
/// </summary>
public class AiConversationDto
{
    public int Id { get; set; }
    public Guid SessionId { get; set; }
    public string Context { get; set; } = "chat";
    public string? Title { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
    public bool IsActive { get; set; }
    public int MessageCount { get; set; }
}

/// <summary>
/// DTO for a conversation with its messages
/// </summary>
public class AiConversationDetailDto
{
    public int Id { get; set; }
    public Guid SessionId { get; set; }
    public string Context { get; set; } = "chat";
    public string? Title { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
    public bool IsActive { get; set; }
    public List<AiConversationMessageDto> Messages { get; set; } = new();
}

/// <summary>
/// DTO for a conversation message
/// </summary>
public class AiConversationMessageDto
{
    public int Id { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int? TokenCount { get; set; }
    public string? Model { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<AiToolExecutionDto> ToolExecutions { get; set; } = new();
}

/// <summary>
/// DTO for a tool execution record
/// </summary>
public class AiToolExecutionDto
{
    public int Id { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string? Arguments { get; set; }
    public string? Result { get; set; }
    public bool WasConfirmed { get; set; }
    public bool? Success { get; set; }
    public int? DurationMs { get; set; }
    public DateTime ExecutedAt { get; set; }
    public AiActionFeedbackDto? Feedback { get; set; }
}

/// <summary>
/// DTO for feedback on an AI action
/// </summary>
public class AiActionFeedbackDto
{
    public int Id { get; set; }
    public bool? ThumbsUp { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ============================================
// Request DTOs
// ============================================

/// <summary>
/// Request to start a new conversation
/// </summary>
public class StartConversationRequest
{
    /// <summary>
    /// Optional session ID. If not provided, a new one will be generated.
    /// </summary>
    public Guid? SessionId { get; set; }
    
    /// <summary>
    /// The context where the conversation is started.
    /// Values: "dashboard", "budget", "savings", "chat", "transactions"
    /// </summary>
    public string Context { get; set; } = "chat";
}

/// <summary>
/// Request to add a message to a conversation
/// </summary>
public class AddMessageRequest
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Request to log a tool execution
/// </summary>
public class LogToolExecutionRequest
{
    public int MessageId { get; set; }
    public Guid SessionId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string? Arguments { get; set; }
    public string? Result { get; set; }
    public bool WasConfirmed { get; set; }
    public bool? Success { get; set; }
    public int? DurationMs { get; set; }
}

/// <summary>
/// Request to submit feedback on a tool execution
/// </summary>
public class SubmitFeedbackRequest
{
    public int ToolExecutionId { get; set; }
    public bool ThumbsUp { get; set; }
    public string? Comment { get; set; }
}

/// <summary>
/// Request to update feedback on a tool execution
/// </summary>
public class UpdateFeedbackRequest
{
    public bool? ThumbsUp { get; set; }
    public string? Comment { get; set; }
}

// ============================================
// Response DTOs
// ============================================

/// <summary>
/// Response containing a list of conversations
/// </summary>
public class ConversationListResponse
{
    public List<AiConversationDto> Conversations { get; set; } = new();
    public int TotalCount { get; set; }
}

/// <summary>
/// Response after starting a conversation
/// </summary>
public class StartConversationResponse
{
    public int ConversationId { get; set; }
    public Guid SessionId { get; set; }
    public string Context { get; set; } = "chat";
    public DateTime StartedAt { get; set; }
}

/// <summary>
/// Response after submitting feedback
/// </summary>
public class FeedbackResponse
{
    public int FeedbackId { get; set; }
    public int ToolExecutionId { get; set; }
    public bool? ThumbsUp { get; set; }
    public DateTime CreatedAt { get; set; }
}
