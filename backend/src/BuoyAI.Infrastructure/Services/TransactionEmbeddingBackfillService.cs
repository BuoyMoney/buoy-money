using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Warms the transaction vector index from categorisations users have ALREADY confirmed.
///
/// The vector fast path in <c>AICategorizationService</c> only learns on write, so an empty
/// index stays cold: every transaction falls through to the LLM, and the index only fills as
/// new transactions happen to arrive. Prod has 774 real transactions and an index that has
/// never held a single vector, so without this the cheapest categorisation path would take
/// months to become useful. This turns it on now.
///
/// Embeds DISTINCT (user, merchant, category) facts rather than every transaction. Measured on
/// prod, 774 transactions collapse to 251 such facts, and the redundant 3-in-4 add nothing: the
/// index is asked "have I seen this merchant, and what did this user call it", which one vector
/// answers as well as fifty.
///
/// Runs once per process start rather than on a loop. It is idempotent (points are keyed by
/// that same tuple, so re-running upserts in place), but there is nothing to do on a second
/// pass unless new confirmations arrived, and those are already embedded on write.
/// </summary>
public class TransactionEmbeddingBackfillService : BackgroundService
{
    // Let migrations, Qdrant collection init and the initial request burst settle first. The
    // collection has to exist before anything can be written into it.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

    // Gemini's embed endpoint is cheap but not free, and this is background work with no user
    // waiting on it. Pace it rather than firing hundreds of requests at once.
    private static readonly TimeSpan BetweenEmbeddings = TimeSpan.FromMilliseconds(200);

    // Ceiling per process start, so a large existing corpus is spread over restarts instead of
    // becoming one enormous burst of API calls.
    private const int MaxPerRun = 1000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TransactionEmbeddingBackfillService> _logger;

    public TransactionEmbeddingBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<TransactionEmbeddingBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            // Backfill is an optimisation. Failing it must never take the app down, and the
            // next process start will try again.
            _logger.LogError(ex, "Transaction embedding backfill failed");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var embeddings = scope.ServiceProvider.GetRequiredService<ITransactionEmbeddingService>();

        // One representative transaction per (user, merchant, category).
        //
        // Only user-confirmed rows: an AI guess is not training signal, and feeding the model's
        // own guesses back in as ground truth would let one bad categorisation reinforce itself.
        // Newest wins, so a merchant the user has since re-filed reflects the current decision.
        var facts = await context.Transactions
            .AsNoTracking()
            .Where(t => t.IsUserCategorized
                        && t.CategoryId != null
                        && t.MerchantName != ""
                        && !t.IsSplit)
            .Join(context.Accounts.AsNoTracking(), t => t.AccountId, a => a.Id, (t, a) => new { t, a.UserId })
            .GroupBy(x => new { x.UserId, x.t.MerchantName, x.t.CategoryId })
            .Select(g => g.OrderByDescending(x => x.t.Date).First())
            .Take(MaxPerRun)
            .ToListAsync(ct);

        if (facts.Count == 0)
        {
            _logger.LogInformation("Transaction embedding backfill: nothing to do");
            return;
        }

        var categoryNames = await context.Categories
            .AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        _logger.LogInformation(
            "Transaction embedding backfill: embedding {Count} distinct merchant/category fact(s)", facts.Count);

        var written = 0;
        var failed = 0;

        foreach (var fact in facts)
        {
            if (ct.IsCancellationRequested) break;

            var categoryId = fact.t.CategoryId!.Value;
            if (!categoryNames.TryGetValue(categoryId, out var categoryName))
            {
                // Category deleted since the user confirmed it. Nothing useful to embed.
                continue;
            }

            try
            {
                await embeddings.StoreTransactionEmbeddingAsync(
                    fact.UserId, fact.t, categoryId, categoryName, fact.t.SemanticTags, ct);
                written++;
            }
            catch (Exception ex)
            {
                // One bad row must not abort the sweep.
                failed++;
                _logger.LogWarning(ex,
                    "Backfill could not embed transaction {TransactionId}", fact.t.Id);
            }

            try
            {
                await Task.Delay(BetweenEmbeddings, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation(
            "Transaction embedding backfill complete: {Written} written, {Failed} failed", written, failed);
    }
}
