using System.Net.Http.Json;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Infrastructure.Telemetry;
using Sentry;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for generating embeddings and storing/searching them in the vector store
/// (Qdrant or pgvector, see <see cref="IVectorStore"/>).
/// Gracefully degrades when no vector store is configured.
/// Uses environment-specific collection names to prevent data collision between staging/production.
/// </summary>
public class ChatEmbeddingService : IChatEmbeddingService
{
    private readonly ILogger<ChatEmbeddingService> _logger;
    private readonly GeminiUsageReporter _geminiUsage;
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly string _collectionName;

    // Embedding needs both a vector store and a Gemini key. Without a key the service is disabled,
    // not broken: it skips the work. Deletes only need the store, so they use _vectorStore.IsEnabled.
    private bool CanEmbed => _vectorStore.IsEnabled && _embeddingClient.IsEnabled;

    public ChatEmbeddingService(
        ILogger<ChatEmbeddingService> logger,
        IVectorStore vectorStore,
        IEmbeddingClient embeddingClient,
        IHostEnvironment hostEnvironment,
        GeminiUsageReporter geminiUsage)
    {
        _logger = logger;
        _geminiUsage = geminiUsage;
        _vectorStore = vectorStore;
        _embeddingClient = embeddingClient;
        
        // Use environment-specific collection name to prevent staging/production data collision
        var environment = hostEnvironment.EnvironmentName.ToLowerInvariant();
        _collectionName = environment switch
        {
            "production" => "chat_history",
            "staging" => "chat_history_staging",
            "development" => "chat_history_dev",
            _ => $"chat_history_{environment}"
        };
        
        if (!_vectorStore.IsEnabled)
        {
            _logger.LogWarning("ChatEmbeddingService initialized without a vector store. Semantic search features disabled.");
        }
        else
        {
            _logger.LogInformation("ChatEmbeddingService initialized with collection '{CollectionName}' for environment '{Environment}'", 
                _collectionName, environment);
        }
    }

    /// <summary>
    /// Generates a 768-dim embedding through the shared <see cref="IEmbeddingClient"/>. Refusals
    /// reach Sentry before the exception is swallowed or reshaped upstream, so "the prepaid
    /// balance ran out" is actionable rather than just another failed embedding.
    /// </summary>
    private async Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        float[]? values;
        try
        {
            values = await _embeddingClient.EmbedAsync(text, cancellationToken);
        }
        catch (EmbeddingApiException ex)
        {
            _geminiUsage.ReportRefusal(ex.StatusCode, "chat-embedding", _embeddingClient.Model, ex.ResponseBody);
            throw;
        }

        if (values != null)
        {
            // Counts toward the same spend metric as chat, so the cost alert sees total
            // Gemini usage rather than only the conversational slice.
            _geminiUsage.RecordEmbedding(
                _embeddingClient.Model, "chat-embedding", GeminiUsageReporter.EstimateTokens(text.Length));
        }
        return values;
    }

    public async Task InitializeCollectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_vectorStore.IsEnabled)
        {
            _logger.LogDebug("Skipping chat collection initialization - vector store not configured");
            return;
        }

        try
        {
            await _vectorStore.EnsureCollectionAsync(
                _collectionName, _embeddingClient.Dimensions, ["user_id", "conversation_id"],
                cancellationToken: cancellationToken);
        }
        catch (Grpc.Core.RpcException grpcEx)
        {
            _logger.LogWarning(grpcEx,
                "Qdrant gRPC connection failed during collection initialization. " +
                "This may indicate Qdrant is unavailable or the connection string uses wrong port (should be 6334 for gRPC). " +
                "StatusCode: {StatusCode}, Detail: {Detail}",
                grpcEx.StatusCode, grpcEx.Status.Detail);
            // Don't throw - collection initialization failure shouldn't crash the app
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize vector collection '{CollectionName}'", _collectionName);
            throw;
        }
    }

    public async Task StoreMessageEmbeddingAsync(
        int userId,
        int conversationId,
        int messageId,
        string content,
        string role,
        DateTime createdAt,
        CancellationToken cancellationToken = default)
    {
        if (!CanEmbed)
        {
            _logger.LogDebug("Skipping message embedding storage - vector store or Gemini API key not configured");
            return;
        }
        
        try
        {
            // Skip very short messages that won't have meaningful embeddings
            if (string.IsNullOrWhiteSpace(content) || content.Length < 10)
            {
                _logger.LogDebug("Skipping embedding for short message {MessageId}", messageId);
                return;
            }

            // Generate embedding using Gemini
            var embedding = await GenerateEmbeddingAsync(content, cancellationToken);

            if (embedding == null || embedding.Length == 0)
            {
                _logger.LogWarning("Failed to generate embedding for message {MessageId}", messageId);
                return;
            }

            await _vectorStore.UpsertAsync(
                _collectionName,
                new VectorPoint(
                    messageId.ToString(),
                    embedding,
                    _embeddingClient.Model,
                    new Dictionary<string, object>
                    {
                        ["user_id"] = userId,
                        ["conversation_id"] = conversationId,
                        ["message_id"] = messageId,
                        ["content"] = content,
                        ["role"] = role,
                        ["created_at"] = createdAt.ToString("O") // ISO 8601 format
                    }),
                cancellationToken);

            _logger.LogDebug("Stored embedding for message {MessageId} in conversation {ConversationId}", 
                messageId, conversationId);
        }
        catch (Grpc.Core.RpcException grpcEx)
        {
            _logger.LogWarning(grpcEx, 
                "Qdrant gRPC connection failed for message {MessageId}. " +
                "This may indicate Qdrant is unavailable or the connection string uses wrong port (should be 6334 for gRPC). " +
                "StatusCode: {StatusCode}, Detail: {Detail}", 
                messageId, grpcEx.StatusCode, grpcEx.Status.Detail);
            // Don't throw - embedding storage should not break chat functionality
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store embedding for message {MessageId}. ExceptionType: {ExType}", 
                messageId, ex.GetType().Name);
            // Don't throw - embedding storage should not break chat functionality
        }
    }

    public async Task<List<ChatSearchResult>> SearchChatHistoryAsync(
        int userId,
        string query,
        int limit = 10,
        float minScore = 0.7f,
        CancellationToken cancellationToken = default)
    {
        if (!CanEmbed)
        {
            _logger.LogDebug("Skipping chat history search - vector store or Gemini API key not configured");
            return [];
        }
        
        try
        {
            // Generate embedding for search query
            var queryEmbedding = await GenerateEmbeddingAsync(query, cancellationToken);

            if (queryEmbedding == null || queryEmbedding.Length == 0)
            {
                _logger.LogWarning("Failed to generate embedding for search query");
                return [];
            }

            // Search with user_id filter for privacy
            var found = await _vectorStore.SearchAsync(
                _collectionName, queryEmbedding, _embeddingClient.Model, limit, minScore, userId,
                cancellationToken);

            var results = found.Select(hit => new ChatSearchResult
            {
                MessageId = hit.GetInt("message_id"),
                ConversationId = hit.GetInt("conversation_id"),
                Content = hit.Get("content"),
                Role = hit.Get("role"),
                CreatedAt = DateTime.Parse(hit.Get("created_at")),
                Score = hit.Score
            }).ToList();

            _logger.LogInformation("Chat history search for user {UserId} returned {Count} results", 
                userId, results.Count);
            return results;
        }
        catch (Grpc.Core.RpcException grpcEx)
        {
            _logger.LogWarning(grpcEx, 
                "Qdrant gRPC connection failed during chat history search for user {UserId}. " +
                "This may indicate Qdrant is unavailable or the connection string uses wrong port (should be 6334 for gRPC). " +
                "StatusCode: {StatusCode}, Detail: {Detail}", 
                userId, grpcEx.StatusCode, grpcEx.Status.Detail);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search chat history for user {UserId}. ExceptionType: {ExType}", 
                userId, ex.GetType().Name);
            return [];
        }
    }

    public async Task DeleteUserEmbeddingsAsync(int userId, CancellationToken cancellationToken = default)
    {
        if (!_vectorStore.IsEnabled)
        {
            _logger.LogDebug("Skipping user embeddings deletion - vector store not configured");
            return;
        }
        
        try
        {
            await _vectorStore.DeleteByFieldAsync(_collectionName, "user_id", userId, cancellationToken);

            _logger.LogInformation("Deleted all embeddings for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete embeddings for user {UserId}", userId);
            throw;
        }
    }

    public async Task DeleteConversationEmbeddingsAsync(int conversationId, CancellationToken cancellationToken = default)
    {
        if (!_vectorStore.IsEnabled)
        {
            _logger.LogDebug("Skipping conversation embeddings deletion - vector store not configured");
            return;
        }
        
        try
        {
            await _vectorStore.DeleteByFieldAsync(_collectionName, "conversation_id", conversationId, cancellationToken);

            _logger.LogInformation("Deleted all embeddings for conversation {ConversationId}", conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete embeddings for conversation {ConversationId}", conversationId);
            throw;
        }
    }
}
