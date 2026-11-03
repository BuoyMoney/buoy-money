using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Controller for managing AI conversation history and feedback.
/// All operations are strictly user-isolated for privacy.
/// </summary>
[Authorize]
public class AiConversationsController : BaseController
{
    private readonly IAiConversationService _conversationService;
    private readonly ILogger<AiConversationsController> _logger;

    public AiConversationsController(
        IAiConversationService conversationService,
        ILogger<AiConversationsController> logger)
    {
        _conversationService = conversationService;
        _logger = logger;
    }

    /// <summary>
    /// Get recent conversations for the current user.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetConversations([FromQuery] int limit = 20)
    {
        var userId = GetCurrentUserId();
        var conversations = await _conversationService.GetRecentConversationsAsync(userId, limit);

        return Ok(new ConversationListResponse
        {
            Conversations = conversations,
            TotalCount = conversations.Count
        });
    }

    /// <summary>
    /// Get a specific conversation by session ID.
    /// </summary>
    [HttpGet("{sessionId:guid}")]
    public async Task<IActionResult> GetConversation(Guid sessionId)
    {
        var userId = GetCurrentUserId();
        var conversation = await _conversationService.GetConversationAsync(userId, sessionId);

        if (conversation == null)
        {
            return NotFound(new { message = "Conversation not found" });
        }

        return Ok(conversation);
    }

    /// <summary>
    /// Start a new conversation or get existing one by session ID.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> StartConversation([FromBody] StartConversationRequest request)
    {
        var userId = GetCurrentUserId();
        var sessionId = request.SessionId ?? Guid.NewGuid();

        var conversation = await _conversationService.StartOrGetConversationAsync(
            userId, sessionId, request.Context);

        return Ok(new StartConversationResponse
        {
            ConversationId = conversation.Id,
            SessionId = conversation.SessionId,
            Context = conversation.Context,
            StartedAt = conversation.StartedAt
        });
    }

    /// <summary>
    /// Update conversation title.
    /// </summary>
    [HttpPatch("{sessionId:guid}/title")]
    public async Task<IActionResult> UpdateTitle(Guid sessionId, [FromBody] UpdateTitleRequest request)
    {
        var userId = GetCurrentUserId();
        var success = await _conversationService.UpdateConversationTitleAsync(
            userId, sessionId, request.Title);

        if (!success)
        {
            return NotFound(new { message = "Conversation not found" });
        }

        return Ok(new { message = "Title updated successfully" });
    }

    /// <summary>
    /// Delete (soft-delete) a conversation.
    /// </summary>
    [HttpDelete("{sessionId:guid}")]
    public async Task<IActionResult> DeleteConversation(Guid sessionId)
    {
        var userId = GetCurrentUserId();
        var success = await _conversationService.DeleteConversationAsync(userId, sessionId);

        if (!success)
        {
            return NotFound(new { message = "Conversation not found" });
        }

        return Ok(new { message = "Conversation deleted successfully" });
    }

    /// <summary>
    /// Submit feedback on a tool execution.
    /// </summary>
    [HttpPost("feedback")]
    public async Task<IActionResult> SubmitFeedback([FromBody] SubmitFeedbackRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var feedback = await _conversationService.SubmitFeedbackAsync(
                request.ToolExecutionId,
                userId,
                request.ThumbsUp,
                request.Comment);

            return Ok(new FeedbackResponse
            {
                FeedbackId = feedback.Id,
                ToolExecutionId = request.ToolExecutionId,
                ThumbsUp = feedback.ThumbsUp,
                CreatedAt = feedback.CreatedAt
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Update existing feedback.
    /// </summary>
    [HttpPatch("feedback/{feedbackId:int}")]
    public async Task<IActionResult> UpdateFeedback(int feedbackId, [FromBody] UpdateFeedbackRequest request)
    {
        var userId = GetCurrentUserId();
        var feedback = await _conversationService.UpdateFeedbackAsync(
            feedbackId,
            userId,
            request.ThumbsUp,
            request.Comment);

        if (feedback == null)
        {
            return NotFound(new { message = "Feedback not found" });
        }

        return Ok(feedback);
    }
}

/// <summary>
/// Request to update conversation title
/// </summary>
public class UpdateTitleRequest
{
    public string Title { get; set; } = string.Empty;
}
