namespace AIHappey.Responses.Diagnostics;

/// <summary>Identifies one native upstream HTTP call, before response normalization.</summary>
public sealed record ResponsesTransportOperation(string? ProviderId, string Operation, string OperationId);

/// <summary>
/// Optional presentation-independent observation of the shared Responses transport.
/// Delivery is awaited; implementations must propagate cancellation and delivery failures.
/// </summary>
public interface IResponsesTransportObserver
{
    bool Enabled { get; }

    ValueTask OnBodyAsync(ResponsesTransportOperation operation, string kind, string content,
        string mediaType, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a non-owning wrapper that observes only the parser's native stream reads.
    /// Disposing the wrapper must not dispose the supplied stream or drain unread input.
    /// </summary>
    Stream ObserveStream(Stream stream, ResponsesTransportOperation operation, string mediaType);
}
