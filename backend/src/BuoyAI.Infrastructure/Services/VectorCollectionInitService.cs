using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

public class VectorCollectionInitService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VectorCollectionInitService> _logger;

    public VectorCollectionInitService(
        IServiceScopeFactory scopeFactory,
        ILogger<VectorCollectionInitService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var chatEmbedding = scope.ServiceProvider.GetRequiredService<IChatEmbeddingService>();
            var transactionEmbedding = scope.ServiceProvider.GetRequiredService<ITransactionEmbeddingService>();

            await Task.WhenAll(
                chatEmbedding.InitializeCollectionAsync(stoppingToken),
                transactionEmbedding.InitializeCollectionAsync(stoppingToken));

            _logger.LogInformation("Vector collections initialized successfully");

            // Help content indexing is best-effort and isolated: a failure here must not
            // affect the chat/transaction collections above. Idempotent (upsert by stable id).
            try
            {
                var helpContent = scope.ServiceProvider.GetRequiredService<IHelpContentService>();
                await helpContent.EnsureCollectionAsync(stoppingToken);
                await helpContent.IndexPublishedArticlesAsync(stoppingToken);
                _logger.LogInformation("Help article index initialized successfully");
            }
            catch (Exception helpEx)
            {
                _logger.LogError(helpEx, "Failed to initialize help article index on startup");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Vector collections on startup");
        }
    }
}
