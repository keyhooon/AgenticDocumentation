using Microsoft.Extensions.Options;
using SemanticRepo.Agent.Indexer.Infrastructure;
using SemanticRepo.Contracts.Indexing;

namespace SemanticRepo.Agent.Indexer.Hosting;

public sealed class RepositoryIndexWorker(
    ICodeIndexAgent agent,
    IOptions<IndexerOptions> options,
    ILogger<RepositoryIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.RepositoryPath))
        {
            logger.LogInformation("No automatic repository configured. IndexRepositoryAsync is available over RPC.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await agent.IndexRepositoryAsync(new IndexRepositoryRequest(settings.RepositoryPath), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Repository indexing failed; retrying after {Seconds} seconds.", settings.ScanIntervalSeconds);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.ScanIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
