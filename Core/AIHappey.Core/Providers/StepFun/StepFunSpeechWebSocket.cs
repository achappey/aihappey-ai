using System.Net.WebSockets;

namespace AIHappey.Core.Providers.StepFun;

/// <summary>
/// Minimal WebSocket abstraction used by StepFun TTS streaming so its protocol
/// handling remains unit-testable without opening a network connection.
/// </summary>
public interface IStepFunSpeechWebSocketFactory
{
    IStepFunSpeechWebSocket Create();
}

public interface IStepFunSpeechWebSocket : IDisposable
{
    WebSocketState State { get; }

    void SetRequestHeader(string name, string value);

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    ValueTask<StepFunSpeechWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken);

    void Abort();
}

public readonly record struct StepFunSpeechWebSocketReceiveResult(
    WebSocketMessageType MessageType,
    int Count,
    bool EndOfMessage);

public sealed class StepFunSpeechWebSocketFactory : IStepFunSpeechWebSocketFactory
{
    public IStepFunSpeechWebSocket Create() => new StepFunSpeechWebSocket();
}

internal sealed class StepFunSpeechWebSocket : IStepFunSpeechWebSocket
{
    private readonly ClientWebSocket _socket = new();

    public WebSocketState State => _socket.State;

    public void SetRequestHeader(string name, string value)
        => _socket.Options.SetRequestHeader(name, value);

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        => _socket.ConnectAsync(uri, cancellationToken);

    public async Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        => await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    public async ValueTask<StepFunSpeechWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await _socket.ReceiveAsync(buffer, cancellationToken);
        return new StepFunSpeechWebSocketReceiveResult(result.MessageType, result.Count, result.EndOfMessage);
    }

    public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => _socket.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public void Abort() => _socket.Abort();

    public void Dispose() => _socket.Dispose();
}
