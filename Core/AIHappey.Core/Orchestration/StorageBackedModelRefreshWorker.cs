using AIHappey.Core.Contracts;
using AIHappey.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AIHappey.Core.Models;

namespace AIHappey.Core.Orchestration;

public sealed class StorageBackedModelRefreshWorker(
    IServiceScopeFactory scopeFactory,
    IModelListingRefreshQueue refreshQueue,
    IOptions<ModelListingStorageOptions> options,
    ILogger<StorageBackedModelRefreshWorker> logger) : BackgroundService
{
    private readonly ModelListingStorageOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!refreshQueue.IsEnabled)
            return;

        using var gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallelBackgroundRefresh), Math.Max(1, _options.MaxParallelBackgroundRefresh));
        var running = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            ModelListingQueueMessage? message;
            try
            {
                message = await refreshQueue.ReceiveAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to receive a model listing refresh queue message; polling will retry.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }

            if (message == null)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }

            await gate.WaitAsync(stoppingToken);
            var task = Task.Run(async () =>
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var resolver = scope.ServiceProvider.GetRequiredService<StorageBackedModelProviderResolver>();
                    await resolver.RefreshQueuedProviderAsync(message.Request, stoppingToken);
                    await refreshQueue.DeleteAsync(message, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Model listing refresh failed for provider {ProviderId}; the queue message will be retried.",
                        message.Request.ProviderId);
                }
                finally
                {
                    gate.Release();
                }
            }, stoppingToken);

            running.Add(task);
            running.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(running);
    }
}
