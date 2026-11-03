namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for generating and searching chat embeddings using vector database.
/// </summary>
public interface IChatEmbeddingService
{
    /// <summary>
    /// Make the vector collection ready if it is not already.
    /// Should be called on application startup.
    /// </summary>
    Task InitializeCollectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate embedding for a message and store it in the vector store.
    /// </summary>
    /// <param name="userId">The user who owns this message</param>
    /// <param name="conversationId">The conversation ID</param>
    /// <param name="messageId">The message ID</param>
    /// <param name="content">The message content</param>
    /// <param name="role">The message role (user/assistant)</param>
    /// <param name="createdAt">When the message was created</param>
    Task StoreMessageEmbeddingAsync(
        int userId,
        int conversationId,
        int messageId,
        string content,
        string role,
        DateTime createdAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for similar messages across user's chat history.
    /// </summary>
    /// <param name="userId">The user to search within (enforces privacy)</param>
    /// <param name="query">The search query</param>
    /// <param name="limit">Maximum number of results</param>
    /// <param name="minScore">Minimum similarity score (0-1)</param>
    /// <returns>List of similar messages with their scores</returns>
    Task<List<ChatSearchResult>> SearchChatHistoryAsync(
        int userId,
        string query,
        int limit = 10,
        float minScore = 0.7f,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all embeddings for a user (for GDPR/data deletion requests).
    /// </summary>
    Task DeleteUserEmbeddingsAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete embeddings for a specific conversation.
    /// </summary>
    Task DeleteConversationEmbeddingsAsync(int conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result from a chat history search.
/// </summary>
public class ChatSearchResult
{
    public int MessageId { get; set; }
    public int ConversationId { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public float Score { get; set; }
}
