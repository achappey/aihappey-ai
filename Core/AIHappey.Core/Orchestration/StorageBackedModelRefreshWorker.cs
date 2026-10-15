using AIHappey.Core.Contracts;
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

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var message = await refreshQueue.ReceiveAsync(stoppingToken);
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
                });

                running.Add(task);

                foreach (var completedTask in running.Where(task => task.IsCompleted).ToArray())
                {
                    try
                    {
                        await completedTask;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "A model listing refresh task failed unexpectedly.");
                    }

                    running.Remove(completedTask);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(running);
        }
    }
}
