using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces;

public interface ITransactionEmbeddingService
{
    Task InitializeCollectionAsync(CancellationToken cancellationToken = default);

    Task StoreTransactionEmbeddingAsync(
        int userId,
        Transaction transaction,
        int categoryId,
        string categoryName,
        string? semanticTags,
        CancellationToken cancellationToken = default);

    Task<List<TransactionSimilarityResult>> FindSimilarCategorizedTransactionsAsync(
        int userId,
        Transaction transaction,
        int limit = 5,
        float minScore = 0.75f,
        CancellationToken cancellationToken = default);

    Task DeleteUserEmbeddingsAsync(int userId, CancellationToken cancellationToken = default);

    Task DeleteTransactionEmbeddingAsync(int transactionId, CancellationToken cancellationToken = default);

}

public class TransactionSimilarityResult
{
    public int TransactionId { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string MerchantName { get; set; } = string.Empty;
    public float Score { get; set; }
    public string? SemanticTags { get; set; }
}
