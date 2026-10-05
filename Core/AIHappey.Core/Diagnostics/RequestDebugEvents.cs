using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Diagnostics;

/// <summary>
/// One instance per DI request scope. Delivery is awaited and serialized: no retained
/// event history, background tasks, queues, or response-wide buffering.
/// </summary>
public sealed class RequestDebugEvents : IProviderDebugEmitter
{
    public const string HeaderName = "X-aihappey-Debug";
    private readonly IProviderDebugSink[] _sinks;
    private readonly List<IProviderDebugSink> _subscriptions = [];
    private readonly SemaphoreSlim _delivery = new(1, 1);
    private long _sequence;

    public RequestDebugEvents(IHttpContextAccessor accessor, IEnumerable<IProviderDebugSink> sinks)
    {
        var context = accessor.HttpContext;
        Enabled = context is not null && IsEnabled(context.Request.Headers);
        RequestId = context?.TraceIdentifier ?? Guid.NewGuid().ToString("n");
        _sinks = sinks.ToArray();
    }

    public bool Enabled { get; }
    public string RequestId { get; }

    public static bool IsEnabled(IHeaderDictionary headers)
        => headers.TryGetValue(HeaderName, out var values)
            && values.Count == 1
            && bool.TryParse(values[0], out var enabled)
            && enabled;

    public IDisposable Subscribe(IProviderDebugSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_subscriptions)
            _subscriptions.Add(sink);
        return new Subscription(this, sink);
    }

    public async ValueTask EmitAsync(string provider, string operation, string operationId, string kind,
        ProviderDebugPayload payload, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            return;

        await _delivery.WaitAsync(cancellationToken);
        try
        {
            var debugEvent = new ProviderDebugEvent(RequestId, ++_sequence, DateTimeOffset.UtcNow,
                provider, operation, operationId, kind, payload);
            IProviderDebugSink[] subscriptions;
            lock (_subscriptions)
                subscriptions = _subscriptions.ToArray();

            foreach (var sink in _sinks.Concat(subscriptions))
                await sink.WriteAsync(debugEvent, cancellationToken);
        }
        finally
        {
            _delivery.Release();
        }
    }

    private sealed class Subscription(RequestDebugEvents owner, IProviderDebugSink sink) : IDisposable
    {
        private RequestDebugEvents? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null)
                lock (current._subscriptions)
                    current._subscriptions.Remove(sink);
        }
    }
}
