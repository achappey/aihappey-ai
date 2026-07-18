using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Cartesia;

public partial class CartesiaProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);

        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechStreamingRequest(options);
        ApplyAuthHeader();

        var payload = BuildOpenAISpeechStreamingPayload(options);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "tts/sse")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json")
        };

        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        ApplyVersionHeader(httpRequest, apiVersion: null);

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"{ProviderName} streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var sawDone = false;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(data))
                continue;

            using var document = ParseCartesiaSpeechStreamEvent(data);
            var root = document.RootElement;
            var eventType = GetCartesiaSpeechStreamString(root, "type");

            switch (eventType)
            {
                case "chunk":
                    var audio = GetCartesiaSpeechStreamString(root, "data");
                    if (string.IsNullOrWhiteSpace(audio))
                        throw new InvalidOperationException("Cartesia speech SSE chunk event did not include audio data.");

                    yield return new AudioSpeechStreamDelta
                    {
                        Audio = NormalizeCartesiaSpeechStreamAudio(audio)
                    };
                    break;

                case "done":
                    sawDone = true;
                    yield return new AudioSpeechStreamDone();
                    yield break;

                case "error":
                    throw new InvalidOperationException(
                        $"{ProviderName} streaming TTS failed: {ReadCartesiaSpeechStreamError(root)}");

                case "timestamps":
                case "phoneme_timestamps":
                    break;

                default:
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(eventType)
                            ? $"Cartesia speech SSE event did not include a type: {data}"
                            : $"Unsupported Cartesia speech SSE event type '{eventType}'.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!sawDone)
            yield return new AudioSpeechStreamDone();
    }

    private static void ValidateOpenAISpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));
    }

    private static void ValidateOpenAISpeechStreamingRequest(AudioSpeechRequest options)
    {
        ValidateOpenAISpeechRequest(options);

        if (!string.Equals(options.StreamFormat?.Trim(), "sse", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Cartesia streaming speech requires stream_format 'sse'.");

        var responseFormat = NormalizeContainer(options.ResponseFormat);
        if (responseFormat is not null and not "raw" and not "pcm")
        {
            throw new NotSupportedException(
                $"Cartesia streaming speech supports only raw or pcm response_format values; received '{options.ResponseFormat}'.");
        }
    }

    private static Dictionary<string, object?> BuildOpenAISpeechStreamingPayload(AudioSpeechRequest options)
    {
        var (ttsModelId, voiceId) = ParseTtsModelAndVoiceFromModel(options.Model);
        if (options.Speed is { } speed && (speed < 0.6f || speed > 1.5f))
            throw new ArgumentOutOfRangeException(nameof(options.Speed), "Cartesia speed must be between 0.6 and 1.5.");

        var payload = new Dictionary<string, object?>
        {
            ["model_id"] = ttsModelId,
            ["transcript"] = options.Input,
            ["voice"] = new Dictionary<string, object?>
            {
                ["mode"] = "id",
                ["id"] = voiceId
            },
            ["output_format"] = new Dictionary<string, object?>
            {
                ["container"] = "raw",
                ["encoding"] = "pcm_s16le",
                ["sample_rate"] = 44100
            }
        };

        if (options.Speed is not null && ttsModelId.StartsWith("sonic-3", StringComparison.OrdinalIgnoreCase))
        {
            payload["generation_config"] = new Dictionary<string, object?>
            {
                ["speed"] = options.Speed.Value
            };
        }

        return payload;
    }

    private static JsonDocument ParseCartesiaSpeechStreamEvent(string data)
    {
        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse Cartesia speech SSE JSON event: {data}", ex);
        }
    }

    private static string? GetCartesiaSpeechStreamString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string NormalizeCartesiaSpeechStreamAudio(string audio)
    {
        try
        {
            return Convert.ToBase64String(Convert.FromBase64String(audio));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Cartesia speech SSE chunk contained invalid base64 audio data.", ex);
        }
    }

    private static string ReadCartesiaSpeechStreamError(JsonElement root)
    {
        var title = GetCartesiaSpeechStreamString(root, "title");
        var message = GetCartesiaSpeechStreamString(root, "message");
        var errorCode = GetCartesiaSpeechStreamString(root, "error_code");

        var description = string.IsNullOrWhiteSpace(title)
            ? message
            : string.IsNullOrWhiteSpace(message)
                ? title
                : $"{title}: {message}";

        return string.IsNullOrWhiteSpace(errorCode)
            ? description ?? root.GetRawText()
            : $"{errorCode}: {description ?? root.GetRawText()}";
    }

}

