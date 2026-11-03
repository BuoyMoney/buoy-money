using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for managing AI conversation history and feedback.
/// All operations enforce strict user isolation for privacy.
/// </summary>
public class AiConversationService : IAiConversationService
{
    private readonly BuoyAIDbContext _context;
    private readonly ILogger<AiConversationService> _logger;
    private readonly BuoyAiMetrics _metrics;
    private readonly IChatEmbeddingService _chatEmbeddingService;
    private readonly IServiceScopeFactory _scopeFactory;

    public AiConversationService(
        BuoyAIDbContext context,
        ILogger<AiConversationService> logger,
        BuoyAiMetrics metrics,
        IChatEmbeddingService chatEmbeddingService,
        IServiceScopeFactory scopeFactory)
    {
        _context = context;
        _logger = logger;
        _metrics = metrics;
        _chatEmbeddingService = chatEmbeddingService;
        _scopeFactory = scopeFactory;
    }

    public async Task<AiConversationDto> StartOrGetConversationAsync(int userId, Guid sessionId, string context = "chat")
    {
        // Check if conversation already exists
        var existing = await _context.AiConversations
            .Where(c => c.UserId == userId && c.SessionId == sessionId)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            return MapToDto(existing);
        }

        // Create new conversation
        var conversation = new AiConversation
        {
            UserId = userId,
            SessionId = sessionId,
            Context = context,
            StartedAt = DateTime.UtcNow,
            LastMessageAt = DateTime.UtcNow,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.AiConversations.Add(conversation);
        await _context.SaveChangesAsync();

        // Record metric for new conversation
        _metrics.RecordAiConversation();

        _logger.LogInformation(
            "Created new AI conversation {SessionId} for user {UserId} in context {Context}",
            sessionId, userId, context);

        return MapToDto(conversation);
    }

    public async Task<AiConversationDetailDto?> GetConversationAsync(int userId, Guid sessionId)
    {
        var conversation = await _context.AiConversations
            .Include(c => c.Messages)
                .ThenInclude(m => m.ToolExecutions)
                    .ThenInclude(t => t.Feedback)
            .Where(c => c.UserId == userId && c.SessionId == sessionId)
            .FirstOrDefaultAsync();

        if (conversation == null)
        {
            return null;
        }

        return MapToDetailDto(conversation);
    }

    public async Task<List<AiConversationDto>> GetRecentConversationsAsync(int userId, int limit = 20)
    {
        var conversations = await _context.AiConversations
            .Where(c => c.UserId == userId && c.IsActive)
            .OrderByDescending(c => c.LastMessageAt)
            .Take(limit)
            .Select(c => new AiConversationDto
            {
                Id = c.Id,
                SessionId = c.SessionId,
                Context = c.Context,
                Title = c.Title,
                StartedAt = c.StartedAt,
                LastMessageAt = c.LastMessageAt,
                IsActive = c.IsActive,
                MessageCount = c.Messages.Count
            })
            .ToListAsync();

        return conversations;
    }

    public async Task<int> AddMessageAsync(
        int conversationId,
        int userId,
        string role,
        string content,
        string? model = null,
        int? tokenCount = null)
    {
        // Verify user owns this conversation
        var conversation = await _context.AiConversations
            .Where(c => c.Id == conversationId && c.UserId == userId)
            .FirstOrDefaultAsync();

        if (conversation == null)
        {
            _logger.LogWarning(
                "User {UserId} attempted to add message to conversation {ConversationId} they don't own",
                userId, conversationId);
            throw new UnauthorizedAccessException("Cannot add message to this conversation");
        }

        var message = new AiConversationMessage
        {
            ConversationId = conversationId,
            Role = role,
            Content = content,
            Model = model,
            TokenCount = tokenCount,
            CreatedAt = DateTime.UtcNow
        };

        _context.AiConversationMessages.Add(message);

        // Update conversation's last message time
        conversation.LastMessageAt = DateTime.UtcNow;
        conversation.UpdatedAt = DateTime.UtcNow;

        // Auto-generate title from first user message if not set
        if (string.IsNullOrEmpty(conversation.Title) && role == "user")
        {
            conversation.Title = SafeText.Ellipsize(content, 50);
        }

        await _context.SaveChangesAsync();

        // Store embedding in vector database for semantic search (fire and forget)
        // Don't block the main flow - embedding storage can be async
        _ = Task.Run(async () =>
        {
            try
            {
                await _chatEmbeddingService.StoreMessageEmbeddingAsync(
                    userId,
                    conversationId,
                    message.Id,
                    content,
                    role,
                    message.CreatedAt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store embedding for message {MessageId}", message.Id);
                // Non-critical failure - don't propagate
            }
        });

        // Extract memories from user messages if user has opted in (fire and forget).
        // CRITICAL: this runs a slow multi-second Gemini call that must NOT touch the request-scoped
        // _context (or scoped services that hold it, e.g. IAiMemoryService) — the request thread keeps
        // using the same DbContext (SaveChanges for the assistant message, usage tracking) and the
        // scope is disposed once the request finishes. Sharing _context here throws "A second
        // operation was started on this DbContext" or ObjectDisposedException. Resolve a fresh
        // DbContext / IAiMemoryService from a new DI scope, capturing only value types beforehand.
        if (role == "user")
        {
            var sessionId = conversation.SessionId;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
                    // Check if user has memory enabled
                    var user = await db.Users.FindAsync(userId);
                    if (user?.AiMemoryEnabled == true)
                    {
                        var aiMemoryService = scope.ServiceProvider.GetRequiredService<IAiMemoryService>();
                        await aiMemoryService.ExtractAndStoreMemoriesAsync(
                            userId,
                            sessionId,
                            content,
                            role);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract memories for user {UserId}", userId);
                    // Non-critical failure - don't propagate
                }
            });
        }

        return message.Id;
    }

    public async Task<int> LogToolExecutionAsync(
        int messageId,
        int userId,
        Guid sessionId,
        string toolName,
        string? arguments,
        string? result,
        bool wasConfirmed,
        bool? success,
        int? durationMs)
    {
        var execution = new AiToolExecution
        {
            MessageId = messageId,
            UserId = userId,
            SessionId = sessionId,
            ToolName = toolName,
            Arguments = arguments,
            Result = result,
            WasConfirmed = wasConfirmed,
            Success = success,
            DurationMs = durationMs,
            ExecutedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        _context.AiToolExecutions.Add(execution);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Logged tool execution: {ToolName} for user {UserId}, success={Success}, duration={DurationMs}ms",
            toolName, userId, success, durationMs);

        return execution.Id;
    }

    public async Task<AiActionFeedbackDto> SubmitFeedbackAsync(
        int toolExecutionId,
        int userId,
        bool thumbsUp,
        string? comment = null)
    {
        // Verify user owns this tool execution
        var execution = await _context.AiToolExecutions
            .Where(t => t.Id == toolExecutionId && t.UserId == userId)
            .FirstOrDefaultAsync();

        if (execution == null)
        {
            _logger.LogWarning(
                "User {UserId} attempted to submit feedback for tool execution {ToolExecutionId} they don't own",
                userId, toolExecutionId);
            throw new UnauthorizedAccessException("Cannot submit feedback for this tool execution");
        }

        // Check if feedback already exists
        var existingFeedback = await _context.AiActionFeedback
            .Where(f => f.ToolExecutionId == toolExecutionId)
            .FirstOrDefaultAsync();

        if (existingFeedback != null)
        {
            // Update existing feedback
            existingFeedback.ThumbsUp = thumbsUp;
            existingFeedback.Comment = comment;
            existingFeedback.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return MapToFeedbackDto(existingFeedback);
        }

        // Create new feedback
        var feedback = new AiActionFeedback
        {
            ToolExecutionId = toolExecutionId,
            UserId = userId,
            ThumbsUp = thumbsUp,
            Comment = comment,
            CreatedAt = DateTime.UtcNow
        };

        _context.AiActionFeedback.Add(feedback);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "User {UserId} submitted feedback for tool execution {ToolExecutionId}: thumbsUp={ThumbsUp}",
            userId, toolExecutionId, thumbsUp);

        return MapToFeedbackDto(feedback);
    }

    public async Task<AiActionFeedbackDto?> UpdateFeedbackAsync(
        int feedbackId,
        int userId,
        bool? thumbsUp,
        string? comment)
    {
        var feedback = await _context.AiActionFeedback
            .Where(f => f.Id == feedbackId && f.UserId == userId)
            .FirstOrDefaultAsync();

        if (feedback == null)
        {
            return null;
        }

        if (thumbsUp.HasValue)
        {
            feedback.ThumbsUp = thumbsUp.Value;
        }

        if (comment != null)
        {
            feedback.Comment = comment;
        }

        feedback.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return MapToFeedbackDto(feedback);
    }

    public async Task<bool> DeleteConversationAsync(int userId, Guid sessionId)
    {
        var conversation = await _context.AiConversations
            .Where(c => c.UserId == userId && c.SessionId == sessionId)
            .FirstOrDefaultAsync();

        if (conversation == null)
        {
            return false;
        }

        // Soft delete by marking inactive
        conversation.IsActive = false;
        conversation.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Drop the conversation's vectors so deleted chat text is not kept (or searched) in the index.
        try
        {
            await _chatEmbeddingService.DeleteConversationEmbeddingsAsync(conversation.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete embeddings for conversation {SessionId}", sessionId);
        }

        _logger.LogInformation(
            "Soft-deleted conversation {SessionId} for user {UserId}",
            sessionId, userId);

        return true;
    }

    public async Task<bool> UpdateConversationTitleAsync(int userId, Guid sessionId, string title)
    {
        var conversation = await _context.AiConversations
            .Where(c => c.UserId == userId && c.SessionId == sessionId)
            .FirstOrDefaultAsync();

        if (conversation == null)
        {
            return false;
        }

        conversation.Title = SafeText.Ellipsize(title, 200);
        conversation.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return true;
    }

    // Mapping helpers
    private static AiConversationDto MapToDto(AiConversation conversation)
    {
        return new AiConversationDto
        {
            Id = conversation.Id,
            SessionId = conversation.SessionId,
            Context = conversation.Context,
            Title = conversation.Title,
            StartedAt = conversation.StartedAt,
            LastMessageAt = conversation.LastMessageAt,
            IsActive = conversation.IsActive,
            MessageCount = conversation.Messages?.Count ?? 0
        };
    }

    private static AiConversationDetailDto MapToDetailDto(AiConversation conversation)
    {
        return new AiConversationDetailDto
        {
            Id = conversation.Id,
            SessionId = conversation.SessionId,
            Context = conversation.Context,
            Title = conversation.Title,
            StartedAt = conversation.StartedAt,
            LastMessageAt = conversation.LastMessageAt,
            IsActive = conversation.IsActive,
            Messages = conversation.Messages
                .OrderBy(m => m.CreatedAt)
                .Select(m => new AiConversationMessageDto
                {
                    Id = m.Id,
                    Role = m.Role,
                    Content = m.Content,
                    TokenCount = m.TokenCount,
                    Model = m.Model,
                    CreatedAt = m.CreatedAt,
                    ToolExecutions = m.ToolExecutions
                        .Select(t => new AiToolExecutionDto
                        {
                            Id = t.Id,
                            ToolName = t.ToolName,
                            Arguments = t.Arguments,
                            Result = t.Result,
                            WasConfirmed = t.WasConfirmed,
                            Success = t.Success,
                            DurationMs = t.DurationMs,
                            ExecutedAt = t.ExecutedAt,
                            Feedback = t.Feedback != null ? MapToFeedbackDto(t.Feedback) : null
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private static AiActionFeedbackDto MapToFeedbackDto(AiActionFeedback feedback)
    {
        return new AiActionFeedbackDto
        {
            Id = feedback.Id,
            ThumbsUp = feedback.ThumbsUp,
            Comment = feedback.Comment,
            CreatedAt = feedback.CreatedAt
        };
    }
}
