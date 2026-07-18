using System.Net.Mime;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Audixa;

public partial class AudixaProvider
{
    private const string AudixaSpeechStreamingEndpoint = "wss://api.audixa.ai/v3/tts/stream";

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);

        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);

        if (!CanUseNativeAudixaSpeechStreaming(options.ResponseFormat))
        {
            var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
            foreach (var streamEvent in response.ToOpenAISpeechStreamEvents())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return streamEvent;
            }

            yield break;
        }

        using var socket = _speechWebSocketFactory.Create();
        await socket.ConnectAsync(BuildAudixaSpeechStreamingUri(), cancellationToken);

        try
        {
            var payload = BuildAudixaSpeechStreamingPayload(options);
            var json = JsonSerializer.Serialize(payload, SpeechJson);
            await socket.SendTextAsync(Encoding.UTF8.GetBytes(json), cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveAudixaSpeechStreamingMessageAsync(socket, cancellationToken);
                if (message is null)
                    throw new InvalidOperationException("Audixa speech streaming WebSocket closed before sending a completed event.");

                if (message.Value.MessageType == WebSocketMessageType.Binary)
                {
                    if (message.Value.Payload.Length > 0)
                    {
                        yield return new AudioSpeechStreamDelta
                        {
                            // Audixa binary chunks are raw 24 kHz mono float32 little-endian PCM.
                            Audio = Convert.ToBase64String(message.Value.Payload)
                        };
                    }

                    continue;
                }

                if (message.Value.MessageType != WebSocketMessageType.Text)
                    throw new InvalidOperationException($"Audixa speech streaming WebSocket returned unsupported message type '{message.Value.MessageType}'.");

                using var document = ParseAudixaSpeechStreamingControlMessage(message.Value.Payload);
                var root = document.RootElement;
                var eventType = ReadString(root, "type");

                switch (eventType)
                {
                    case "started":
                        break;

                    case "completed":
                        yield return new AudioSpeechStreamDone();
                        yield break;

                    case "error":
                        throw new InvalidOperationException($"Audixa streaming TTS failed: {ReadAudixaSpeechStreamingError(root)}");

                    default:
                        if (string.IsNullOrWhiteSpace(eventType))
                            throw new InvalidOperationException("Audixa speech streaming control message did not include a type.");

                        throw new InvalidOperationException($"Unsupported Audixa speech streaming control message type '{eventType}'.");
                }
            }
        }
        finally
        {
            await CloseAudixaSpeechStreamingSocketAsync(socket, cancellationToken);
        }
    }

    private static void ValidateOpenAISpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));

        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));
    }

    private Uri BuildAudixaSpeechStreamingUri()
        => new($"{AudixaSpeechStreamingEndpoint}?api_key={Uri.EscapeDataString(ResolveApiKey())}");

    private Dictionary<string, object?> BuildAudixaSpeechStreamingPayload(AudioSpeechRequest options)
    {
        var (model, modelVoice) = ResolveModelAndVoice(options.Model);
        var voice = (modelVoice ?? options.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Audixa requires a voice. Provide AudioSpeechRequest.voice or an Audixa voice shortcut in the model id.", nameof(options));

        var payload = new Dictionary<string, object?>
        {
            ["text"] = options.Input,
            ["voice_id"] = voice,
            ["model"] = model,
            // Audixa supports wav/mp3 request values, but always streams raw float32 PCM chunks.
            ["audio_format"] = "wav"
        };

        if (options.Speed is not null)
            payload["speed"] = options.Speed.Value;

        return payload;
    }

    private static bool CanUseNativeAudixaSpeechStreaming(string? responseFormat)
        => responseFormat?.Trim().ToLowerInvariant() is "pcm" or "raw";

    private static async Task<(WebSocketMessageType MessageType, byte[] Payload)?> ReceiveAudixaSpeechStreamingMessageAsync(
        IAudixaSpeechWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        WebSocketMessageType? messageType = null;

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (messageType is not null && messageType != result.MessageType)
                throw new InvalidOperationException("Audixa speech streaming WebSocket changed message type before a message was complete.");

            messageType ??= result.MessageType;
            message.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                return (messageType.Value, message.ToArray());
        }
    }

    private static JsonDocument ParseAudixaSpeechStreamingControlMessage(byte[] payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse Audixa speech streaming control message: {Encoding.UTF8.GetString(payload)}", ex);
        }
    }

    private static string ReadAudixaSpeechStreamingError(JsonElement root)
        => ReadString(root, "message")
           ?? ReadString(root, "error")
           ?? root.GetRawText();

    private static async Task CloseAudixaSpeechStreamingSocketAsync(
        IAudixaSpeechWebSocket socket,
        CancellationToken cancellationToken)
    {
        if (socket.State is not WebSocketState.Open and not WebSocketState.CloseReceived)
            return;

        try
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "speech stream completed",
                cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken);
        }
        catch
        {
            socket.Abort();
        }
    }

}

