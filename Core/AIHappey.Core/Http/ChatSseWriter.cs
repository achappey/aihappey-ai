using System.Text.Json;
using AIHappey.Core.Diagnostics;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Core.Http;

/// <summary>
/// The chat presentation adapter. One serialized writer handles both debug and normal
/// parts; providers never write to the HTTP response. Dispose after streaming (and errors).
/// </summary>
public sealed class ChatSseWriter : IProviderDebugSink, IDisposable
{
    public const string DebugPartType = "data-aihappey-debug";
    private readonly HttpResponse _response;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly IDisposable? _subscription;

    public ChatSseWriter(HttpContext context)
    {
        _response = context.Response;
        var events = context.RequestServices.GetRequiredService<RequestDebugEvents>();
        if (events.Enabled)
            _subscription = events.Subscribe(this);
    }

    public async ValueTask WriteAsync(UIMessagePart part, CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            await _response.WriteAsync($"data: {JsonSerializer.Serialize(part, JsonSerializerOptions.Web)}\n\n", cancellationToken);
            await _response.Body.FlushAsync(cancellationToken);
        }
        finally
        {
            _writes.Release();
        }
    }

    public ValueTask WriteAsync(ProviderDebugEvent debugEvent, CancellationToken cancellationToken)
        => WriteAsync(new DataUIPart
        {
            Type = DebugPartType,
            Id = $"{debugEvent.RequestId}:{debugEvent.Sequence}",
            Data = debugEvent,
            Transient = false
        }, cancellationToken);

    public void Dispose() => _subscription?.Dispose();
}
