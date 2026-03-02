using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.Venice;

public partial class VeniceProvider
{
    private async Task<TranscriptionResponse> VeniceTranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(audioString);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Audio must be base64 or a data-url containing base64.", ex);
        }

        var model = !string.IsNullOrWhiteSpace(TryGetString(metadata, "model"))
            ? TryGetString(metadata, "model")!.Trim()
            : !string.IsNullOrWhiteSpace(request.Model)
                ? request.Model.Trim()
                : "nvidia/parakeet-tdt-0.6b-v3";

        var responseFormat = !string.IsNullOrWhiteSpace(TryGetString(metadata, "response_format"))
            ? TryGetString(metadata, "response_format")!.Trim().ToLowerInvariant()
            : "json";

        var timestamps = TryGetBoolean(metadata, "timestamps");
        var language = TryGetString(metadata, "language")?.Trim();

        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var audioContent = new ByteArrayContent(bytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(audioContent, "file", fileName);

        form.Add(new StringContent(model, Encoding.UTF8), "model");
        form.Add(new StringContent(responseFormat, Encoding.UTF8), "response_format");

        if (timestamps is not null)
            form.Add(new StringContent(timestamps.Value ? "true" : "false", Encoding.UTF8), "timestamps");

        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language, Encoding.UTF8), "language");

        AddMetadataPassthroughFields(form, metadata);

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Venice transcription request failed ({(int)response.StatusCode}): {raw}");

        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        var isPlainText = contentType == "text/plain" || string.Equals(responseFormat, "text", StringComparison.OrdinalIgnoreCase);

        var parsed = isPlainText
            ? ParseTextTranscription(raw)
            : ParseJsonTranscription(raw);

        var providerMetadata = new JsonObject
        {
            ["endpoint"] = "v1/audio/transcriptions",
            ["status"] = (int)response.StatusCode,
            ["contentType"] = response.Content.Headers.ContentType?.MediaType,
            ["request"] = new JsonObject
            {
                ["model"] = model,
                ["response_format"] = responseFormat,
                ["timestamps"] = timestamps,
                ["language"] = language
            }
        };

        if (metadata.ValueKind == JsonValueKind.Object)
            providerMetadata["passthrough"] = JsonNode.Parse(metadata.GetRawText());

        return new TranscriptionResponse
        {
            Text = parsed.Text,
            DurationInSeconds = parsed.Duration,
            Language = parsed.Language ?? language,
            Segments = parsed.Segments,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMetadata, JsonSerializerOptions.Web)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = model,
                Body = isPlainText
                    ? raw
                    : JsonSerializer.Deserialize<object>(raw, JsonSerializerOptions.Web) ?? raw
            }
        };
    }

    private static (string Text, float? Duration, string? Language, List<TranscriptionSegment> Segments) ParseTextTranscription(string raw)
        => (raw ?? string.Empty, null, null, []);

    private static (string Text, float? Duration, string? Language, List<TranscriptionSegment> Segments) ParseJsonTranscription(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var text = TryGetString(root, "text") ?? string.Empty;
        var duration = TryGetFloat(root, "duration");
        var language = TryGetString(root, "language");
        var segments = ExtractSegments(root);

        if (string.IsNullOrWhiteSpace(text) && segments.Count > 0)
            text = string.Join(" ", segments.Select(s => s.Text));

        return (text, duration, language, segments);
    }

    private static List<TranscriptionSegment> ExtractSegments(JsonElement root)
    {
        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("timestamps", out var timestampsEl)
            && timestampsEl.ValueKind == JsonValueKind.Object
            && timestampsEl.TryGetProperty("segment", out var segmentEl)
            && segmentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in segmentEl.EnumerateArray())
            {
                var text = TryGetString(item, "text");
                var start = TryGetFloat(item, "start");
                var end = TryGetFloat(item, "end");

                if (string.IsNullOrWhiteSpace(text) || start is null || end is null)
                    continue;

                segments.Add(new TranscriptionSegment
                {
                    Text = text,
                    StartSecond = start.Value,
                    EndSecond = end.Value
                });
            }
        }

        if (segments.Count > 0)
            return segments;

        if (root.TryGetProperty("segments", out var fallbackSegments)
            && fallbackSegments.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in fallbackSegments.EnumerateArray())
            {
                var text = TryGetString(item, "text");
                var start = TryGetFloat(item, "start");
                var end = TryGetFloat(item, "end");

                if (string.IsNullOrWhiteSpace(text) || start is null || end is null)
                    continue;

                segments.Add(new TranscriptionSegment
                {
                    Text = text,
                    StartSecond = start.Value,
                    EndSecond = end.Value
                });
            }
        }

        return segments;
    }

    private static void AddMetadataPassthroughFields(MultipartFormDataContent form, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.NameEquals("file")
                || property.NameEquals("model")
                || property.NameEquals("response_format")
                || property.NameEquals("timestamps")
                || property.NameEquals("language"))
            {
                continue;
            }

            if (TanscTryToFormString(property.Value, out var value))
                form.Add(new StringContent(value, Encoding.UTF8), property.Name);
        }
    }

    private static bool TanscTryToFormString(JsonElement value, out string result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                result = value.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                result = value.ToString();
                return true;
            default:
                result = string.Empty;
                return false;
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString();
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            return value.GetBoolean();

        return null;
    }

    private static float? TryGetFloat(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;

        return (float)value.GetDouble();
    }
}
