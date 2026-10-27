using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    private sealed record MistralAudioEvent(string? Type, JsonElement Payload);

    private async Task<HttpResponseMessage> SendAudioStreamingAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyAuthHeader();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Mistral streaming audio request failed ({(int)response.StatusCode}): {error}");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Mistral streaming audio returned '{contentType}' instead of SSE: {error}");
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // The iterator owns the response body, but not the response. Its caller owns
    // the request/response so early disposal also releases the HTTP connection.
    private static async IAsyncEnumerable<MistralAudioEvent> ReadAudioEventsAsync(
        HttpResponseMessage response,
        string doneType,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();
        string? eventName = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || line.Length == 0)
            {
                var item = ParseAudioEvent(eventName, dataLines, doneType);
                dataLines.Clear();
                eventName = null;
                if (item is not null)
                {
                    ValidateAudioEvent(item);
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                    if (item.Type == doneType)
                        yield break;
                }

                if (line is null)
                    throw new InvalidOperationException($"Mistral audio stream ended before '{doneType}'.");
                continue;
            }

            if (line.StartsWith(':'))
                continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..]; // SSE removes one optional space, not payload whitespace.
            if (field == "event")
                eventName = value;
            else if (field == "data")
                dataLines.Add(value);
        }
    }

    private static MistralAudioEvent? ParseAudioEvent(string? eventName, List<string> dataLines, string doneType)
    {
        var data = string.Join("\n", dataLines);
        // An SSE error's contents need not be a JSON object. Do not invent an
        // error schema or turn an error into a successful empty result.
        if (eventName == "error")
            throw new InvalidOperationException($"Mistral audio stream provider error: {data}");
        if (data.Length == 0)
            return null;
        if (data.Trim() == "[DONE]")
            throw new InvalidOperationException($"Mistral audio stream ended before '{doneType}'.");

        using var document = JsonDocument.Parse(data);
        var payload = document.RootElement;
        if (payload.ValueKind != JsonValueKind.Object)
            throw new JsonException("Mistral audio event must be a JSON object.");
        var type = AudioString(payload, "type") ?? eventName;
        if (type == "error" ||
            (payload.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null))
            throw new InvalidOperationException($"Mistral audio stream provider error: {data}");
        if (!string.IsNullOrEmpty(eventName) && type != eventName)
            throw new JsonException("Mistral audio SSE event name does not match its payload type.");

        // JsonElements must remain usable after both parsing and enumeration.
        return new MistralAudioEvent(type, payload.Clone());
    }

    private static void ValidateAudioEvent(MistralAudioEvent item)
    {
        var payload = item.Payload;
        switch (item.Type)
        {
            case "speech.audio.delta":
                _ = Convert.FromBase64String(RequiredAudioString(payload, "audio_data"));
                break;
            case "transcription.text.delta":
                _ = RequiredAudioString(payload, "text");
                break;
            case "transcription.language":
                _ = AudioString(payload, "language");
                break;
            case "transcription.segment.delta":
                ValidateAudioSegment(payload);
                break;
            case "transcription.done":
                _ = RequiredAudioString(payload, "text");
                _ = AudioString(payload, "model");
                _ = AudioString(payload, "language");
                if (payload.TryGetProperty("segments", out var segments) && segments.ValueKind != JsonValueKind.Null)
                {
                    if (segments.ValueKind != JsonValueKind.Array)
                        throw new JsonException("Mistral transcription segments must be an array.");
                    foreach (var segment in segments.EnumerateArray())
                        ValidateAudioSegment(segment);
                }
                ValidateAudioUsage(payload);
                break;
            case "speech.audio.done":
                ValidateAudioUsage(payload);
                break;
        }
    }

    private static void ValidateAudioSegment(JsonElement segment)
    {
        if (segment.ValueKind != JsonValueKind.Object)
            throw new JsonException("Mistral transcription segment must be an object.");
        _ = RequiredAudioString(segment, "text");
        _ = AudioNumber(segment, "start");
        _ = AudioNumber(segment, "end");
        _ = AudioString(segment, "speaker_id");
    }

    private static void ValidateAudioUsage(JsonElement payload)
    {
        if (payload.TryGetProperty("usage", out var usage) &&
            usage.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
            throw new JsonException("Mistral audio usage must be an object or null.");
    }

    private static string RequiredAudioString(JsonElement payload, string name)
        => AudioString(payload, name) ?? throw new JsonException($"Mistral audio event is missing '{name}'.");

    private static string? AudioString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new JsonException($"Mistral audio field '{name}' must be a string or null.");
    }

    private static double? AudioNumber(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            return number;
        throw new JsonException($"Mistral audio field '{name}' must be a finite number or null.");
    }

    private static long? AudioCount(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0)
            return count;
        throw new JsonException($"Mistral audio usage '{name}' must be a non-negative integer or null.");
    }

    private static Dictionary<string, JsonElement> AudioMetadata(JsonElement payload)
        => new() { ["mistral"] = payload.Clone() };

    private static Dictionary<string, JsonElement> AudioProviderOptions(Dictionary<string, JsonElement>? options)
    {
        if (options?.TryGetValue("mistral", out var native) != true || native.ValueKind == JsonValueKind.Null)
            return new(StringComparer.Ordinal);
        if (native.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.mistral must be an object.", nameof(options));
        return native.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static string NormalizeAudioModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        model = model.Trim();
        const string prefix = "mistral/";
        var result = model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        return result;
    }
}
