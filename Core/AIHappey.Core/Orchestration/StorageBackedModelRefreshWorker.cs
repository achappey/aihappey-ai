using AIHappey.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using AIHappey.Core.Models;

namespace AIHappey.Core.Orchestration;

public sealed class StorageBackedModelRefreshWorker(
    IServiceScopeFactory scopeFactory,
    IModelListingRefreshQueue refreshQueue,
    IOptions<ModelListingStorageOptions> options) : BackgroundService
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
                    using var scope = scopeFactory.CreateScope();
                    var resolver = scope.ServiceProvider.GetRequiredService<StorageBackedModelProviderResolver>();
                    await resolver.RefreshQueuedProviderAsync(message.Request, stoppingToken);
                    await refreshQueue.DeleteAsync(message, stoppingToken);
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
