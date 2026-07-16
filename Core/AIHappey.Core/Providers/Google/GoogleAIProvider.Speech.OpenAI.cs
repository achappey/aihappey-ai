using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));

        var speechResponse = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return speechResponse.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));

        ApplyAuthHeader();

        var warnings = new List<object>();
        var payload = BuildOpenAISpeechStreamingPayload(options, warnings);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, InteractionsRelativeUrl);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        httpRequest.Headers.TryAddWithoutValidation("Api-Revision", "2026-05-20");
        httpRequest.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        httpRequest.Content = new StringContent(payload.ToJsonString(GoogleSpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{Google} streaming speech failed ({(int)response.StatusCode}): {error}");
        }

        AudioSpeechUsage? usage = null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while (!cancellationToken.IsCancellationRequested
               && (line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
                continue;

            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                break;

            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(data);
                root = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Failed to parse Google speech SSE json event: {data}", ex);
            }

            usage = TryExtractGoogleSpeechStreamUsage(root) ?? usage;

            if (TryExtractGoogleSpeechStreamAudio(root, out var audio))
            {
                yield return new AudioSpeechStreamDelta
                {
                    Audio = audio
                };
            }
        }

        yield return new AudioSpeechStreamDone
        {
            Usage = usage
        };
    }

    private static JsonObject BuildOpenAISpeechStreamingPayload(AudioSpeechRequest options, ICollection<object> warnings)
    {
        var payload = BuildSpeechPayload(options.ToSpeechRequest(), warnings);
        payload["stream"] = true;

        return payload;
    }

    private static bool TryExtractGoogleSpeechStreamAudio(JsonElement root, out string audio)
    {
        audio = string.Empty;

        if (TryIsGoogleSpeechStepDelta(root, out var delta)
            && TryExtractAudioDataFromGoogleSpeechStreamDelta(delta, out audio))
        {
            return true;
        }

        if (TryGetProperty(root, "delta", out delta)
            && TryExtractAudioDataFromGoogleSpeechStreamDelta(delta, out audio))
        {
            return true;
        }

        return false;
    }

    private static bool TryIsGoogleSpeechStepDelta(JsonElement root, out JsonElement delta)
    {
        delta = default;

        var eventType = TryGetString(root, "event_type") ?? TryGetString(root, "eventType") ?? TryGetString(root, "type");
        if (!string.Equals(eventType, "step.delta", StringComparison.OrdinalIgnoreCase))
            return false;

        return TryGetProperty(root, "delta", out delta);
    }

    private static bool TryExtractAudioDataFromGoogleSpeechStreamDelta(JsonElement delta, out string audio)
    {
        audio = string.Empty;

        if (delta.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in delta.EnumerateArray())
            {
                if (TryExtractAudioDataFromGoogleSpeechStreamDelta(item, out audio))
                    return true;
            }

            return false;
        }

        if (delta.ValueKind != JsonValueKind.Object)
            return false;

        var type = TryGetString(delta, "type");
        var data = TryGetString(delta, "data") ?? TryGetString(delta, "audio");
        var mimeType = TryGetString(delta, "mime_type") ?? TryGetString(delta, "mimeType");

        if (string.IsNullOrWhiteSpace(data))
            return false;

        if (!string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(mimeType) || !mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        audio = data;
        return true;
    }

    private static AudioSpeechUsage? TryExtractGoogleSpeechStreamUsage(JsonElement root)
    {
        if (!TryGetProperty(root, "usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        return new AudioSpeechUsage
        {
            InputTokens = TryGetGoogleSpeechUsageInt(usage, "input_tokens", "inputTokens", "prompt_token_count", "promptTokenCount"),
            OutputTokens = TryGetGoogleSpeechUsageInt(usage, "output_tokens", "outputTokens", "candidates_token_count", "candidatesTokenCount"),
            TotalTokens = TryGetGoogleSpeechUsageInt(usage, "total_tokens", "totalTokens", "total_token_count", "totalTokenCount")
        };
    }

    private static int? TryGetGoogleSpeechUsageInt(JsonElement usage, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(usage, propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
                return value;

            if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
                return value;
        }

        return null;
    }
}
