using AIHappey.Core.Contracts;

namespace AIHappey.Core.Storage;

public sealed class NullModelListingRefreshQueue : IModelListingRefreshQueue
{
    public bool IsEnabled => false;

    public Task EnqueueAsync(ModelListingRefreshRequest request, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<ModelListingQueueMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<ModelListingQueueMessage?>(null);

    public Task DeleteAsync(ModelListingQueueMessage message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
