using System.Net.WebSockets;

namespace AIHappey.Core.Providers.Audixa;

/// <summary>
/// A narrowly-scoped WebSocket abstraction for Audixa speech streaming.
/// It keeps the provider's transport testable without coupling its HTTP client
/// or public provider contract to a particular WebSocket implementation.
/// </summary>
public interface IAudixaSpeechWebSocketFactory
{
    IAudixaSpeechWebSocket Create();
}

public interface IAudixaSpeechWebSocket : IDisposable
{
    WebSocketState State { get; }

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    ValueTask<AudixaSpeechWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken);

    void Abort();
}

public readonly record struct AudixaSpeechWebSocketReceiveResult(
    WebSocketMessageType MessageType,
    int Count,
    bool EndOfMessage);

public sealed class AudixaSpeechWebSocketFactory : IAudixaSpeechWebSocketFactory
{
    public IAudixaSpeechWebSocket Create() => new AudixaSpeechWebSocket();
}

internal sealed class AudixaSpeechWebSocket : IAudixaSpeechWebSocket
{
    private readonly ClientWebSocket _socket = new();

    public WebSocketState State => _socket.State;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        => _socket.ConnectAsync(uri, cancellationToken);

    public async Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        => await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);

    public async ValueTask<AudixaSpeechWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await _socket.ReceiveAsync(buffer, cancellationToken);
        return new AudixaSpeechWebSocketReceiveResult(result.MessageType, result.Count, result.EndOfMessage);
    }

    public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => _socket.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public void Abort() => _socket.Abort();

    public void Dispose() => _socket.Dispose();
}
