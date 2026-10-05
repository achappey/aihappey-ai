using System.Collections.Frozen;

namespace AIHappey.Windows;

/// <summary>
/// Supplies local headers before HeaderAuth creates its per-request snapshot.
/// Explicit headers (including empty ones) win without modifying shared defaults.
/// </summary>
public sealed class LocalHeaderDefaultsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly FrozenDictionary<string, string> _headers;

    public LocalHeaderDefaultsMiddleware(RequestDelegate next, IReadOnlyDictionary<string, string> headers)
    {
        _next = next;
        _headers = headers.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public Task InvokeAsync(HttpContext context)
    {
        foreach (var (name, value) in _headers)
        {
            if (!context.Request.Headers.ContainsKey(name))
                context.Request.Headers[name] = value;
        }

        return _next(context);
    }
}
