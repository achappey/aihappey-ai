namespace AIHappey.Responses.Diagnostics;

/// <summary>
/// An optional observer for the current async execution flow. Hosts must begin the scope
/// synchronously in the caller's flow and dispose it after awaited transport execution.
/// No provider instances, event history, or process-wide observer are retained.
/// </summary>
public sealed class ResponsesTransportScope : IDisposable
{
    private static readonly AsyncLocal<ResponsesTransportScope?> Ambient = new();
    private readonly ResponsesTransportScope? _previous;
    private IResponsesTransportObserver? _observer;
    private bool _disposed;

    private ResponsesTransportScope(IResponsesTransportObserver? observer)
    {
        _previous = Ambient.Value;
        _observer = observer;
        Ambient.Value = this;
    }

    /// <summary>Disabled or disposed scopes never expose an observer.</summary>
    public static IResponsesTransportObserver? Current
        => Ambient.Value?._observer is { Enabled: true } observer ? observer : null;

    /// <summary>A null observer explicitly masks any enclosing scope.</summary>
    public static ResponsesTransportScope Begin(IResponsesTransportObserver? observer)
        => new(observer);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        // Invalidate inherited contexts as well, so later child work cannot retain a sink.
        _observer = null;
        if (ReferenceEquals(Ambient.Value, this))
            Ambient.Value = _previous;
    }
}
