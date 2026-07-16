using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.XAI;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SpaceXAI;

public partial class SpaceXAIProvider
{
    private const string XaiTtsWebSocketEndpoint = "wss://api.x.ai/v1/tts";

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

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {ResolveXaiApiKey()}");

        await socket.ConnectAsync(BuildOpenAISpeechStreamingUri(options), cancellationToken);

        try
        {
            await SendXaiSpeechStreamingTextFrameAsync(
                socket,
                new { type = "text.delta", delta = options.Input },
                cancellationToken);

            await SendXaiSpeechStreamingTextFrameAsync(
                socket,
                new { type = "text.done" },
                cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveXaiSpeechStreamingTextFrameAsync(socket, cancellationToken);
                if (message is null)
                    yield break;

                JsonElement root;
                try
                {
                    using var document = JsonDocument.Parse(message);
                    root = document.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"Failed to parse {ProviderName} speech WebSocket event: {message}", ex);
                }

                var eventType = root.TryGetString("type");
                switch (eventType)
                {
                    case "audio.delta":
                        var audio = root.TryGetString("delta");
                        if (!string.IsNullOrWhiteSpace(audio))
                        {
                            yield return new AudioSpeechStreamDelta
                            {
                                Audio = NormalizeXaiSpeechAudioDelta(audio)
                            };
                        }

                        break;

                    case "audio.done":
                        yield return new AudioSpeechStreamDone();
                        yield break;

                    case "audio.clear":
                        break;

                    case "error":
                        throw new InvalidOperationException($"{ProviderName} streaming speech failed: {ReadXaiSpeechStreamingError(root)}");

                    default:
                        if (!string.IsNullOrWhiteSpace(eventType))
                            throw new InvalidOperationException($"Unsupported {ProviderName} speech WebSocket event type '{eventType}'.");

                        throw new InvalidOperationException($"{ProviderName} speech WebSocket event did not include a type: {message}");
                }
            }
        }
        finally
        {
            await CloseXaiSpeechStreamingSocketAsync(socket, cancellationToken);
        }
    }

    private string ResolveXaiApiKey()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(SpaceXAI)} API key.");

        return key;
    }

    private static void ValidateOpenAISpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));

        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));
    }

    private static Uri BuildOpenAISpeechStreamingUri(AudioSpeechRequest options)
    {
        var speechRequest = options.ToSpeechRequest();
        var metadata = default(XAISpeechProviderMetadata);
        var warnings = new List<object>();
        var selection = ParseSpeechModel(options.Model);

        var language = ResolveLanguage(selection, speechRequest, metadata, warnings);
        var voice = ResolveVoiceId(selection, speechRequest, metadata, warnings);
        var codec = NormalizeOpenAISpeechStreamingCodec(options.ResponseFormat);

        var parameters = new List<KeyValuePair<string, string?>>
        {
            new("language", language),
            new("voice", voice),
            new("codec", codec)
        };

        if (options.Speed is { } speed)
            parameters.Add(new("speed", NormalizeOpenAISpeechStreamingSpeed(speed)));

        var query = string.Join(
            "&",
            parameters
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
                .Select(parameter => $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value!)}"));

        return new Uri($"{XaiTtsWebSocketEndpoint}?{query}");
    }

    private static string NormalizeOpenAISpeechStreamingCodec(string? responseFormat)
    {
        var codec = NormalizeCodec(responseFormat) ?? "mp3";
        return codec switch
        {
            "mp3" or "wav" or "pcm" or "mulaw" or "alaw" => codec,
            _ => throw new ArgumentException($"Unsupported {ProviderName} streaming speech response_format '{responseFormat}'.", nameof(responseFormat))
        };
    }

    private static string NormalizeOpenAISpeechStreamingSpeed(float speed)
    {
        if (speed is < 0.7f or > 1.5f)
            throw new ArgumentException($"{ProviderName} streaming speech supports speed values from 0.7 to 1.5.", nameof(speed));

        return speed.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static async Task SendXaiSpeechStreamingTextFrameAsync(
        ClientWebSocket socket,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, XaiSpeechJsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task<string?> ReceiveXaiSpeechStreamingTextFrameAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException($"{ProviderName} speech WebSocket returned a non-text frame.");

            message.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    private static string NormalizeXaiSpeechAudioDelta(string audio)
    {
        try
        {
            return Convert.ToBase64String(Convert.FromBase64String(audio));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{ProviderName} speech WebSocket returned an invalid base64 audio delta.", ex);
        }
    }

    private static string ReadXaiSpeechStreamingError(JsonElement root)
        => root.TryGetString("message")
           ?? root.TryGetString("error")
           ?? root.GetRawText();

    private static async Task CloseXaiSpeechStreamingSocketAsync(
        ClientWebSocket socket,
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
