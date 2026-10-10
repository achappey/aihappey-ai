using AIHappey.Responses.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Core.Diagnostics;

/// <summary>Adapts native Responses captures to the existing request-scoped debug publisher.</summary>
public sealed class ResponsesTransportDebugObserver(IProviderDebugEmitter emitter) : IResponsesTransportObserver
{
    public bool Enabled => emitter.Enabled;

    /// <summary>
    /// Call synchronously in the chat action, after provider resolution and SSE header setup.
    /// The caller owns the scope; no subscription or client output is added by this adapter.
    /// </summary>
    public static ResponsesTransportScope Begin(HttpContext context)
    {
        var emitter = context.RequestServices.GetRequiredService<IProviderDebugEmitter>();
        return ResponsesTransportScope.Begin(emitter.Enabled ? new ResponsesTransportDebugObserver(emitter) : null);
    }

    public ValueTask OnBodyAsync(ResponsesTransportOperation operation, string kind, string content,
        string mediaType, CancellationToken cancellationToken)
        => Enabled
            ? emitter.EmitAsync(operation.ProviderId ?? string.Empty, operation.Operation, operation.OperationId,
                kind, ProviderDebugPayload.FromText(content, mediaType), cancellationToken)
            : ValueTask.CompletedTask;

    public Stream ObserveStream(Stream stream, ResponsesTransportOperation operation, string mediaType)
        => new DebugResponseStream(stream, emitter, operation.ProviderId ?? string.Empty,
            operation.Operation, operation.OperationId, mediaType);
}
