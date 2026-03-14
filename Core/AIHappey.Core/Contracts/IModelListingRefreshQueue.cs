using AIHappey.Core.Storage;

namespace AIHappey.Core.Contracts;

public interface IModelListingRefreshQueue
{
    bool IsEnabled { get; }

    Task EnqueueAsync(ModelListingRefreshRequest request, CancellationToken cancellationToken = default);

    Task<ModelListingQueueMessage?> ReceiveAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(ModelListingQueueMessage message, CancellationToken cancellationToken = default);
}
