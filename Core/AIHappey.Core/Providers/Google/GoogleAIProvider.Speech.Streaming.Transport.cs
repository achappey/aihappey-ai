using System.Globalization;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    // Protocol references: https://ai.google.dev/gemini-api/docs/speech-generation
    // and https://ai.google.dev/api/interactions (step.delta, metadata.total_usage,
    // interaction.completed). Streaming defaults to headerless 24 kHz mono PCM.
    private sealed record GoogleSpeechAudioChunk(string? Id, string Data, AudioFormat Format, JsonElement Payload);
    private sealed record GoogleSpeechStreamEvent(string? Type, JsonElement Payload, IReadOnlyList<GoogleSpeechAudioChunk> Audio);

    private static HttpRequestMessage CreateGoogleSpeechStreamMessage(JsonObject payload)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, InteractionsRelativeUrl);
        message.Headers.Accept.ParseAdd("text/event-stream");
        message.Headers.TryAddWithoutValidation("Api-Revision", "2026-05-20");
        message.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        message.Content = new StringContent(payload.ToJsonString(GoogleSpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json);
        return message;
    }

    private async Task<HttpResponseMessage> SendGoogleSpeechStreamAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyAuthHeader();
        var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Google streaming speech failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}");
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Google streaming speech returned '{contentType}' instead of SSE: {await response.Content.ReadAsStringAsync(cancellationToken)}");
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // The adapters own the request/response. Disposing either iterator early also
    // disposes this body, so an abandoned synthesis cannot retain its connection.
    private static async IAsyncEnumerable<GoogleSpeechStreamEvent> ReadGoogleSpeechEventsAsync(
        HttpResponseMessage response, GoogleSpeechStreamState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var data = new List<string>();
        string? eventName = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || line.Length == 0)
            {
                var item = ParseGoogleSpeechEvent(eventName, data, state);
                data.Clear();
                eventName = null;
                if (item is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                    if (item.Type == "interaction.completed")
                        yield break;
                }
                if (line is null)
                    throw new InvalidOperationException("Google speech stream ended before interaction.completed.");
                continue;
            }
            if (line.StartsWith(':'))
                continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];
            if (field == "event")
                eventName = value;
            else if (field == "data")
                data.Add(value);
        }
    }

    private static GoogleSpeechStreamEvent? ParseGoogleSpeechEvent(string? eventName, List<string> lines, GoogleSpeechStreamState state)
    {
        var data = string.Join("\n", lines);
        if (eventName == "error")
            throw new InvalidOperationException($"Google speech stream provider error: {data}");
        if (data.Length == 0)
            return null;
        if (data.Trim() == "[DONE]")
            throw new InvalidOperationException("Google speech stream ended before interaction.completed.");
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement.Clone();
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Google speech SSE event must be an object.");
        var type = TryGetString(root, "event_type") ?? TryGetString(root, "eventType") ?? TryGetString(root, "type") ?? eventName;
        if (!string.IsNullOrEmpty(eventName) && eventName != type)
            throw new JsonException("Google speech SSE event name does not match its payload type.");
        if (type is "error" or "interaction.failed" or "interaction.cancelled" or "interaction.incomplete" ||
            (TryGetProperty(root, "error", out var error) && error.ValueKind != JsonValueKind.Null))
            throw new InvalidOperationException($"Google speech stream provider error: {data}");

        var interaction = TryGetProperty(root, "interaction", out var nested) ? nested : root;
        var status = TryGetString(interaction, "status") ?? TryGetString(root, "status");
        if (status is "failed" or "cancelled" or "incomplete" or "requires_action" ||
            (type == "interaction.completed" && status is not (null or "completed")))
            throw new InvalidOperationException($"Google speech did not complete successfully: {data}");
        state.UpdateUsage(root);
        if (interaction.ValueKind == JsonValueKind.Object)
            state.UpdateUsage(interaction);
        state.ServiceTier = TryGetString(interaction, "service_tier") ?? TryGetString(root, "service_tier") ?? state.ServiceTier;

        var audio = new List<GoogleSpeechAudioChunk>();
        // Do not scan completed interaction snapshots: they repeat earlier audio.
        if (type == "step.delta" && TryGetProperty(root, "delta", out var delta))
        {
            var id = TryGetProperty(root, "index", out var index) ? index.ToString() : null;
            foreach (var block in delta.ValueKind == JsonValueKind.Array ? delta.EnumerateArray().ToArray() : [delta])
                state.ReadAudio(block, id, audio);
        }
        if (type == "interaction.completed" && !state.HasAudio)
            throw new InvalidOperationException("Google speech completed without audio data.");
        return new GoogleSpeechStreamEvent(type, root, audio);
    }

    private sealed class GoogleSpeechStreamState
    {
        private readonly JsonObject usage = [];
        private readonly Dictionary<string, AudioFormat> formats = new(StringComparer.Ordinal);
        private readonly AudioFormat defaultFormat;
        public bool HasAudio { get; private set; }
        public string? ServiceTier { get; set; }
        public JsonElement? Usage => usage.Count == 0 ? null : JsonSerializer.SerializeToElement(usage);
        public JsonElement MetadataRoot => JsonSerializer.SerializeToElement(new { usage = Usage, service_tier = ServiceTier });

        public GoogleSpeechStreamState(JsonObject payload)
        {
            var format = GoogleSpeechResponseFormat(payload);
            defaultFormat = ReadGoogleSpeechFormat(JsonSerializer.SerializeToElement(format ?? new JsonObject()), new AudioFormat { Type = "audio/l16", Rate = 24000 });
            ServiceTier = payload["service_tier"]?.GetValue<string>();
        }

        public void UpdateUsage(JsonElement root)
        {
            if (TryGetProperty(root, "usage", out var reported) && reported.ValueKind != JsonValueKind.Null)
                Merge(reported);
            if (TryGetProperty(root, "metadata", out var metadata) && TryGetProperty(metadata, "total_usage", out reported) && reported.ValueKind != JsonValueKind.Null)
                Merge(reported);
        }

        private void Merge(JsonElement reported)
        {
            if (reported.ValueKind != JsonValueKind.Object)
                throw new JsonException("Google speech usage must be an object.");
            foreach (var field in reported.EnumerateObject())
                usage[field.Name] = JsonNode.Parse(field.Value.GetRawText());
        }

        public void ReadAudio(JsonElement block, string? id, List<GoogleSpeechAudioChunk> audio)
        {
            if (block.ValueKind != JsonValueKind.Object)
                throw new JsonException("Google speech delta must be an object.");
            if (TryGetString(block, "type") != "audio")
                return;
            var key = id ?? "";
            var format = ReadGoogleSpeechFormat(block, formats.GetValueOrDefault(key) ?? defaultFormat);
            formats[key] = format;
            if (!TryGetProperty(block, "data", out var data) || data.ValueKind == JsonValueKind.Null)
            {
                if (TryGetProperty(block, "uri", out _))
                    throw new NotSupportedException("Google streaming speech returned an audio URI instead of inline audio.");
                return; // Metadata-only audio delta; remember its format for later bytes.
            }
            if (data.ValueKind != JsonValueKind.String)
                throw new JsonException("Google speech audio data must be a base64 string.");
            var value = data.GetString()!;
            if (value.Length > 0)
            {
                if (Convert.FromBase64String(value).Length == 0)
                    throw new JsonException("Google speech returned an empty audio chunk.");
                HasAudio = true;
            }
            audio.Add(new GoogleSpeechAudioChunk(id, value, format, block.Clone()));
        }
    }

    private static AudioFormat ReadGoogleSpeechFormat(JsonElement source, AudioFormat fallback)
    {
        var mime = TryGetString(source, "mime_type") ?? TryGetString(source, "mimeType");
        var rate = GoogleSpeechCount(source, "sample_rate", "sampleRate", "rate");
        var type = fallback.Type;
        if (mime is not null)
        {
            var parsed = new ContentType(mime);
            if (!parsed.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                throw new JsonException("Google speech returned a non-audio MIME type.");
            type = parsed.MediaType;
            if (rate is null && parsed.Parameters["rate"] is { } parameter)
                rate = long.Parse(parameter, CultureInfo.InvariantCulture);
        }
        if (rate is <= 0 or > int.MaxValue)
            throw new JsonException("Google speech sample rate must be a positive integer.");
        return new AudioFormat { Type = type, Rate = (int?)rate ?? fallback.Rate };
    }

    private static long? GoogleSpeechCount(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(root, name, out var value) || value.ValueKind == JsonValueKind.Null)
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0)
                return count;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count >= 0)
                return count;
            throw new JsonException($"Google speech '{name}' must be a non-negative integer.");
        }
        return null;
    }

    private static long? GoogleSpeechInputTokens(JsonElement usage)
        => GoogleSpeechCount(usage, "total_input_tokens", "input_tokens", "inputTokens", "prompt_token_count", "promptTokenCount");

    private static long? GoogleSpeechOutputTokens(JsonElement usage)
        => GoogleSpeechCount(usage, "total_output_tokens", "output_tokens", "outputTokens", "candidates_token_count", "candidatesTokenCount");
}
