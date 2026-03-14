namespace AIHappey.Core.Storage;

public sealed class ModelListingQueueMessage
{
    public required string MessageId { get; init; }

    public required string PopReceipt { get; init; }

    public required ModelListingRefreshRequest Request { get; init; }
}
