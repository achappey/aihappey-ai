using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.StepFun;

public partial class StepFunProvider
{
    private const string StepFunSpeechStreamingEndpoint = "wss://api.stepfun.ai/v1/realtime/audio";

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!CanUseNativeStepFunSpeechStreaming(options))
        {
            var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
            foreach (var streamEvent in response.ToOpenAISpeechStreamEvents())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return streamEvent;
            }

            yield break;
        }

        var (model, voice) = ResolveStepFunSpeechStreamingModelAndVoice(options);
        var responseFormat = ResolveStepFunSpeechStreamingResponseFormat(options.ResponseFormat);

        using var socket = _speechWebSocketFactory.Create();
        socket.SetRequestHeader("Authorization", $"Bearer {ResolveApiKey()}");
        await socket.ConnectAsync(BuildStepFunSpeechStreamingUri(model), cancellationToken);

        try
        {
            var sessionId = await ReceiveStepFunSpeechConnectionSessionIdAsync(socket, cancellationToken);
            await SendStepFunSpeechWebSocketEventAsync(socket, new
            {
                type = "tts.create",
                data = BuildStepFunSpeechCreateData(sessionId, voice, responseFormat, options)
            }, cancellationToken);

            await ReceiveStepFunSpeechSessionCreatedAsync(socket, sessionId, cancellationToken);

            await SendStepFunSpeechWebSocketEventAsync(socket, new
            {
                type = "tts.text.delta",
                data = new { session_id = sessionId, text = options.Input }
            }, cancellationToken);

            await SendStepFunSpeechWebSocketEventAsync(socket, new
            {
                type = "tts.text.done",
                data = new { session_id = sessionId }
            }, cancellationToken);

            while (true)
            {
                using var document = await ReceiveStepFunSpeechJsonEventAsync(socket, cancellationToken);
                var root = document.RootElement;
                var eventType = ReadStepFunSpeechString(root, "type");

                switch (eventType)
                {
                    case "tts.response.sentence.start":
                    case "tts.response.sentence.end":
                    case "tts.text.flushed":
                        break;

                    case "tts.response.audio.delta":
                        var audio = ReadStepFunSpeechDataString(root, "audio");
                        if (!string.IsNullOrWhiteSpace(audio))
                        {
                            yield return new AudioSpeechStreamDelta
                            {
                                Audio = NormalizeStepFunSpeechAudio(audio)
                            };
                        }

                        break;

                    case "tts.response.audio.done":
                        yield return new AudioSpeechStreamDone();
                        yield break;

                    case "tts.response.error":
                        throw new InvalidOperationException($"StepFun streaming TTS failed: {ReadStepFunSpeechError(root)}");

                    default:
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(eventType)
                                ? "StepFun speech WebSocket event did not include a type."
                                : $"Unsupported StepFun speech WebSocket event type '{eventType}'.");
                }
            }
        }
        finally
        {
            await CloseStepFunSpeechWebSocketAsync(socket, cancellationToken);
        }
    }

    private string ResolveApiKey()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(StepFun)} API key.");

        return key;
    }

    private bool CanUseNativeStepFunSpeechStreaming(AudioSpeechRequest options)
    {
        if (string.IsNullOrWhiteSpace(options.Model) || string.IsNullOrWhiteSpace(options.Input))
            return false;

        var (model, voice) = ResolveStepFunSpeechStreamingModelAndVoice(options, validate: false);
        return !string.IsNullOrWhiteSpace(voice)
               && model is "step-tts-2" or "stepaudio-2.5-tts"
               && ResolveStepFunSpeechStreamingResponseFormat(options.ResponseFormat, validate: false) is not null;
    }

    private (string Model, string? Voice) ResolveStepFunSpeechStreamingModelAndVoice(AudioSpeechRequest options, bool validate = true)
    {
        var (model, modelVoice) = ParseSpeechModelAndVoice(options.Model);
        var voice = (modelVoice ?? options.Voice)?.Trim();

        if (validate && string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Voice is required for StepFun speech streaming.", nameof(options));

        return (model.Trim().ToLowerInvariant(), voice);
    }

    private static string ResolveStepFunSpeechStreamingResponseFormat(string? responseFormat, bool validate = true)
    {
        var normalized = string.IsNullOrWhiteSpace(responseFormat) ? "mp3" : responseFormat.Trim().ToLowerInvariant();
        if (normalized is "mp3" or "opus" or "flac" or "wav" or "pcm")
            return normalized;

        if (validate)
            throw new ArgumentException($"Unsupported StepFun speech streaming response_format '{responseFormat}'.", nameof(responseFormat));

        return null!;
    }

    private static Uri BuildStepFunSpeechStreamingUri(string model)
        => new($"{StepFunSpeechStreamingEndpoint}?model={Uri.EscapeDataString(model)}");

    private static Dictionary<string, object?> BuildStepFunSpeechCreateData(
        string sessionId,
        string voice,
        string responseFormat,
        AudioSpeechRequest options)
    {
        var data = new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["voice_id"] = voice,
            ["response_format"] = responseFormat,
            ["speed_ratio"] = options.Speed is { } speed ? Math.Clamp(speed, 0.5f, 2f) : null,
            ["mode"] = "sentence"
        };

        return data;
    }

    private static async Task SendStepFunSpeechWebSocketEventAsync(
        IStepFunSpeechWebSocket socket,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, StepFunSpeechJson);
        await socket.SendTextAsync(Encoding.UTF8.GetBytes(json), cancellationToken);
    }

    private static async Task<string> ReceiveStepFunSpeechConnectionSessionIdAsync(
        IStepFunSpeechWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var document = await ReceiveStepFunSpeechJsonEventAsync(socket, cancellationToken);
        var root = document.RootElement;

        if (!string.Equals(ReadStepFunSpeechString(root, "type"), "tts.connection.done", StringComparison.Ordinal))
            throw new InvalidOperationException("StepFun speech WebSocket did not send a tts.connection.done event.");

        return ReadStepFunSpeechDataString(root, "session_id")
               ?? throw new InvalidOperationException("StepFun speech connection event did not include a session_id.");
    }

    private static async Task ReceiveStepFunSpeechSessionCreatedAsync(
        IStepFunSpeechWebSocket socket,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var document = await ReceiveStepFunSpeechJsonEventAsync(socket, cancellationToken);
        var root = document.RootElement;
        var eventType = ReadStepFunSpeechString(root, "type");

        if (eventType == "tts.response.error")
            throw new InvalidOperationException($"StepFun streaming TTS failed: {ReadStepFunSpeechError(root)}");

        if (!string.Equals(eventType, "tts.response.created", StringComparison.Ordinal))
            throw new InvalidOperationException("StepFun speech WebSocket did not send a tts.response.created event.");

        var createdSessionId = ReadStepFunSpeechDataString(root, "session_id");
        if (!string.Equals(createdSessionId, sessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("StepFun speech session creation event referenced an unexpected session_id.");
    }

    private static async Task<JsonDocument> ReceiveStepFunSpeechJsonEventAsync(
        IStepFunSpeechWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("StepFun speech WebSocket closed before sending a completed event.");

            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("StepFun speech WebSocket returned a non-text event.");

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                try
                {
                    return JsonDocument.Parse(message.ToArray());
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"Failed to parse StepFun speech WebSocket event: {Encoding.UTF8.GetString(message.ToArray())}", ex);
                }
            }
        }
    }

    private static string NormalizeStepFunSpeechAudio(string audio)
    {
        try
        {
            return Convert.ToBase64String(Convert.FromBase64String(audio));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("StepFun speech WebSocket returned an invalid base64 audio delta.", ex);
        }
    }

    private static string? ReadStepFunSpeechString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadStepFunSpeechDataString(JsonElement root, string propertyName)
        => root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static string ReadStepFunSpeechError(JsonElement root)
    {
        var message = ReadStepFunSpeechDataString(root, "message") ?? root.GetRawText();
        var code = ReadStepFunSpeechDataString(root, "code");
        return string.IsNullOrWhiteSpace(code) ? message : $"{code}: {message}";
    }

    private static async Task CloseStepFunSpeechWebSocketAsync(
        IStepFunSpeechWebSocket socket,
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

