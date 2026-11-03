using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

public class TransactionEmbeddingService : ITransactionEmbeddingService
{
    private readonly ILogger<TransactionEmbeddingService> _logger;
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly BuoyAiMetrics _metrics;
    private readonly string _collectionName;

    /// <summary>
    /// 768, down from 3072.
    ///
    /// Qdrant keeps vectors resident in RAM by default (no on_disk, no quantization), so the
    /// dimension is a direct RAM cost: 3072 dims x 4 bytes = 12.3KB of memory per transaction,
    /// before HNSW links. At ~110 transactions per user that was ~1.4MB/user, which makes
    /// Qdrant the first hard memory wall on a 4GB box at around a thousand users.
    ///
    /// 768 x 4 = 3KB, a 4x reduction, and it matches what ChatEmbeddingService and
    /// HelpContentService already use. gemini-embedding-2 supports Matryoshka truncation, so a
    /// shorter vector is a supported output rather than a lossy crop, and the inputs here are
    /// short merchant strings where 3072 dims buys nothing measurable.
    ///
    /// Changed now, while the collection is still empty, because a dimension change forces a
    /// recreate. Doing it after a backfill would mean re-embedding everything twice.
    ///
    /// Next lever if this ever needs to go further: scalar (int8) quantization with rescoring
    /// takes the resident cost to ~768 bytes/transaction, another 4x. Deliberately not stacked
    /// on top of this change, so quality is proven at 768 before a recall variable is added to a
    /// pipeline that has never successfully stored a single vector.
    /// </summary>
    private int EmbeddingDimensions => _embeddingClient.Dimensions;
    private string EmbeddingModelName => _embeddingClient.Model;

    /// Embeddings are input-only and priced well below chat. Kept alongside the
    /// chat rates in AIService.Context.CalculateCost — both are estimates for
    /// alerting, not billing.
    private const decimal EmbeddingCostPer1kTokens = 0.00002m;

    private readonly IUserRepository _users;

    public TransactionEmbeddingService(
        ILogger<TransactionEmbeddingService> logger,
        IVectorStore vectorStore,
        IEmbeddingClient embeddingClient,
        IHostEnvironment hostEnvironment,
        BuoyAiMetrics metrics,
        IUserRepository users)
    {
        _logger = logger;
        _users = users;
        _metrics = metrics;
        _vectorStore = vectorStore;
        _embeddingClient = embeddingClient;

        var environment = hostEnvironment.EnvironmentName.ToLowerInvariant();
        _collectionName = environment switch
        {
            "production" => "transaction_categories",
            "staging" => "transaction_categories_staging",
            "development" => "transaction_categories_dev",
            _ => $"transaction_categories_{environment}"
        };

        if (!_vectorStore.IsEnabled)
        {
            _logger.LogWarning("TransactionEmbeddingService initialized without a vector store. Vector similarity categorization disabled.");
        }
        else
        {
            _logger.LogInformation("TransactionEmbeddingService initialized with collection '{CollectionName}'", _collectionName);
        }
    }

    private async Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        if (!_embeddingClient.IsEnabled)
        {
            _logger.LogWarning("Gemini API key not configured, cannot generate transaction embedding");
            return null;
        }

        float[]? values;
        try
        {
            values = await _embeddingClient.EmbedAsync(text, cancellationToken);
        }
        catch (EmbeddingApiException ex)
        {
            var status = ex.StatusCode;
            _logger.LogError("embedContent returned {Status}: {Body}", status, ex.ResponseBody);

            // 429 = quota exhausted, 403 = key rejected/billing disabled. Under prepaid
            // billing these are the shapes "credits ran out" arrives in, and they need to
            // reach a human fast: the alert on spend rate catches a runaway BEFORE the
            // balance is gone, this catches the case where it already is. Reported to
            // Sentry explicitly rather than relying on log capture, and fingerprinted so
            // every occurrence groups into one actionable issue instead of scattering by
            // response body.
            if (status == 429 || status == 403)
            {
                SentrySdk.CaptureMessage(
                    $"Gemini embedContent refused with {status} - check prepaid balance",
                    scope =>
                    {
                        scope.SetTag("gemini.status", status.ToString());
                        scope.SetTag("gemini.surface", "embedding");
                        scope.SetTag("gemini.model", EmbeddingModelName);
                        scope.SetFingerprint(new[] { "gemini-quota", status.ToString() });
                    },
                    SentryLevel.Error);
            }
            return null;
        }

        // Count embedding spend toward the same counter as chat and insights.
        //
        // This path calls Gemini over direct REST and never touched AIUsageTracker, so
        // embeddings billed against the prepaid key while being invisible to both
        // AIUsageRecords and the cost metric. That mattered most for the workload most
        // likely to run away: TransactionEmbeddingBackfillService re-embeds every
        // transaction in bulk, and it was the one thing the alert could not see.
        if (values != null)
        {
            // ~4 chars per token is the usual rough conversion; embeddings are
            // input-only, so there is no output side to price.
            var tokens = Math.Max(1, text.Length / 4);
            var cost = (tokens / 1000m) * EmbeddingCostPer1kTokens;
            _metrics.RecordAiUsage(EmbeddingModelName, "embedding", tokens, cost);
        }

        return values;
    }



    /// <summary>
    /// Stable point id for a (user, merchant, category) fact.
    ///
    /// Deterministic so re-confirming the same merchant upserts in place instead of adding a
    /// near-duplicate vector, which is what makes the backfill idempotent and safe to re-run.
    /// Merchant is normalised so "STARBUCKS #1234" and "Starbucks" do not become two facts.
    /// </summary>
    internal static string MerchantPointId(int userId, string? merchantName, int categoryId)
    {
        var key = $"{userId}|{NormalizeMerchant(merchantName)}|{categoryId}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        // First 16 bytes of the digest as a UUID: Qdrant accepts a UUID point id, and 128 bits
        // makes a collision irrelevant at any scale this will ever see.
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }

    /// <summary>
    /// Collapses the store-number and reference noise Plaid puts in merchant names, so repeat
    /// visits to one merchant map to one fact.
    /// </summary>
    internal static string NormalizeMerchant(string? merchantName)
    {
        if (string.IsNullOrWhiteSpace(merchantName)) return "";
        var lowered = merchantName.Trim().ToLowerInvariant();
        // Strip digits and punctuation runs ("starbucks #1234" -> "starbucks").
        var cleaned = new string(lowered.Where(c => char.IsLetter(c) || char.IsWhiteSpace(c)).ToArray());
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string BuildEmbeddingText(Transaction transaction, string categoryName, string? semanticTags)
    {
        var amount = Math.Abs(transaction.Amount);
        var tagsSection = string.IsNullOrWhiteSpace(semanticTags) ? "" : $" | Tags: {semanticTags}";
        return $"task: sentence similarity | query: Merchant: {transaction.MerchantName}{tagsSection} | Amount: ${amount:F2} | Category: {categoryName}";
    }

    private static string BuildSearchText(Transaction transaction)
    {
        var amount = Math.Abs(transaction.Amount);
        var tagsSection = string.IsNullOrWhiteSpace(transaction.SemanticTags) ? "" : $" | Tags: {transaction.SemanticTags}";
        return $"task: sentence similarity | query: Merchant: {transaction.MerchantName}{tagsSection} | Amount: ${amount:F2}";
    }

    public async Task InitializeCollectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_vectorStore.IsEnabled) return;

        try
        {
            // Payload here is a handful of ids used for filtering, not something worth holding in
            // RAM alongside the vectors (Qdrant). A dimension change rebuilds the collection:
            // otherwise every upsert would fail on a mismatch.
            await _vectorStore.EnsureCollectionAsync(
                _collectionName, EmbeddingDimensions, ["user_id", "category_id"],
                onDiskPayload: true, recreateOnDimensionChange: true, cancellationToken: cancellationToken);
        }
        catch (Grpc.Core.RpcException grpcEx)
        {
            _logger.LogWarning(grpcEx,
                "Qdrant gRPC connection failed during collection initialization. StatusCode: {StatusCode}",
                grpcEx.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize vector collection '{CollectionName}'", _collectionName);
            throw;
        }
    }

    /// <summary>
    /// 5.1.2(i): embedding sends a transaction's merchant, tags, amount and category to Gemini,
    /// so it needs the same consent as every other AI path. Checked here, the one choke point,
    /// rather than at each caller (accept/reject learning, recategorize, the startup backfill
    /// that walks every user).
    /// </summary>
    private async Task<bool> HasConsentAsync(int userId) =>
        AiConsent.IsCurrent(await _users.GetByIdAsync(userId));

    public async Task StoreTransactionEmbeddingAsync(
        int userId,
        Transaction transaction,
        int categoryId,
        string categoryName,
        string? semanticTags,
        CancellationToken cancellationToken = default)
    {
        if (!_vectorStore.IsEnabled || !_embeddingClient.IsEnabled) return;
        if (!await HasConsentAsync(userId)) return;

        try
        {
            var text = BuildEmbeddingText(transaction, categoryName, semanticTags);
            var embedding = await GenerateEmbeddingAsync(text, cancellationToken);

            if (embedding == null || embedding.Length == 0)
            {
                _logger.LogWarning("Failed to generate embedding for transaction {TransactionId}", transaction.Id);
                return;
            }

            // Keyed by (user, merchant, category), NOT by transaction id.
            //
            // One point per transaction meant the index grew with users x TIME, forever:
            // measured at ~90 transactions/user/month against only ~15 new merchants/user
            // /month, so roughly 6 in 7 vectors were restating something already known.
            // Keying on the merchant collapses those into one upsert, and the index then
            // grows with a user's distinct merchants, which saturates.
            //
            // It is also the more correct shape for what this index is asked. The consensus
            // rule in TryVectorSimilarityCategorizationAsync wants "do the nearest matches
            // AGREE on a category", and that question is meaningless when the top five hits
            // are five copies of the same merchant voting with themselves.
            await _vectorStore.UpsertAsync(
                _collectionName,
                new VectorPoint(
                    MerchantPointId(userId, transaction.MerchantName, categoryId),
                    embedding,
                    _embeddingClient.Model,
                    new Dictionary<string, object>
                    {
                        ["user_id"] = userId,
                        ["category_id"] = categoryId,
                        ["category_name"] = categoryName,
                        ["merchant_name"] = transaction.MerchantName ?? "",
                        ["semantic_tags"] = semanticTags ?? "",
                        ["confirmed_at"] = DateTime.UtcNow.ToString("O"),
                        // Which transaction last refreshed this merchant fact. Diagnostic only:
                        // the point represents the merchant, not this one transaction.
                        ["last_transaction_id"] = transaction.Id
                    }),
                cancellationToken);

            _logger.LogDebug("Stored transaction embedding {TransactionId}: {Merchant} → {Category} (Tags: {Tags})",
                transaction.Id, transaction.MerchantName, categoryName, semanticTags ?? "none");
        }
        catch (Grpc.Core.RpcException grpcEx)
        {
            _logger.LogWarning(grpcEx,
                "Qdrant gRPC connection failed storing embedding for transaction {TransactionId}. StatusCode: {StatusCode}",
                transaction.Id, grpcEx.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store embedding for transaction {TransactionId}", transaction.Id);
        }
    }

    public async Task<List<TransactionSimilarityResult>> FindSimilarCategorizedTransactionsAsync(
        int userId,
        Transaction transaction,
        int limit = 5,
        float minScore = 0.75f,
        CancellationToken cancellationToken = default)
    {
        if (!_vectorStore.IsEnabled || !_embeddingClient.IsEnabled) return [];
        if (!await HasConsentAsync(userId)) return [];

        try
        {
            var searchText = BuildSearchText(transaction);
            var queryEmbedding = await GenerateEmbeddingAsync(searchText, cancellationToken);

            if (queryEmbedding == null || queryEmbedding.Length == 0)
            {
                _logger.LogWarning("Failed to generate search embedding for transaction {TransactionId}", transaction.Id);
                return [];
            }

            var found = await _vectorStore.SearchAsync(
                _collectionName, queryEmbedding, _embeddingClient.Model, limit, minScore, userId,
                cancellationToken);

            var results = found.Select(hit => new TransactionSimilarityResult
            {
                // Points are keyed by merchant fact, not transaction, so this has always been 0.
                TransactionId = 0,
                CategoryId = hit.GetInt("category_id"),
                CategoryName = hit.Get("category_name"),
                MerchantName = hit.Get("merchant_name"),
                Score = hit.Score,
                SemanticTags = hit.Get("semantic_tags")
            }).ToList();

            _logger.LogDebug("Vector similarity search for {Merchant} (user {UserId}) returned {Count} results",
                transaction.MerchantName, userId, results.Count);
            return results;
        }
        catch (Grpc.Core.RpcException grpcEx)
        {
            _logger.LogWarning(grpcEx,
                "Qdrant gRPC connection failed during similarity search. StatusCode: {StatusCode}",
                grpcEx.StatusCode);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search similar transactions for user {UserId}", userId);
            return [];
        }
    }

    public async Task DeleteUserEmbeddingsAsync(int userId, CancellationToken cancellationToken = default)
    {
        if (!_vectorStore.IsEnabled) return;

        try
        {
            await _vectorStore.DeleteByFieldAsync(_collectionName, "user_id", userId, cancellationToken);

            _logger.LogInformation("Deleted all transaction embeddings for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete transaction embeddings for user {UserId}", userId);
            throw;
        }
    }

    // Points are keyed by (user, merchant, category), not by transaction, so nothing is stored under
    // a transaction id. This has never matched a point; deleting one transaction must not forget the
    // merchant fact, which other transactions still rely on. Kept for the interface.
    public Task DeleteTransactionEmbeddingAsync(int transactionId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
