using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider
{
    private async Task<TranscriptionResponse> TranscriptionRequestInternal(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        var bytes = Convert.FromBase64String(audioString);
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model, Encoding.UTF8), "model");

        var cortecsOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        AddRawCortecsPassthrough(form, cortecsOptions);

        using var resp = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Cortecs transcription request failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var segments = ParseSegments(root);
        var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString() ?? string.Empty
            : string.Join(" ", segments.Select(a => a.Text));

        var language = root.TryGetProperty("language", out var languageEl) && languageEl.ValueKind == JsonValueKind.String
            ? languageEl.GetString()
            : null;

        float? duration = null;
        if (root.TryGetProperty("usage", out var usageEl)
            && usageEl.ValueKind == JsonValueKind.Object
            && usageEl.TryGetProperty("audio_duration_seconds", out var durationEl)
            && durationEl.ValueKind == JsonValueKind.Number)
        {
            duration = (float)durationEl.GetDouble();
        }

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                    ? modelEl.GetString() ?? request.Model
                    : request.Model,
                Body = JsonSerializer.Deserialize<object>(raw, JsonSerializerOptions.Web) ?? raw
            }
        };
    }

    private static List<TranscriptionSegment> ParseSegments(JsonElement root)
    {
        var segments = new List<TranscriptionSegment>();

        if (!root.TryGetProperty("segments", out var segmentsEl) || segmentsEl.ValueKind != JsonValueKind.Array)
            return segments;

        foreach (var segment in segmentsEl.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object)
                continue;

            var text = segment.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                ? textEl.GetString() ?? string.Empty
                : string.Empty;

            var start = TryReadFloat(segment, "start", "start_second", "startSecond");
            var end = TryReadFloat(segment, "end", "end_second", "endSecond");

            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (end < start)
                end = start;

            segments.Add(new TranscriptionSegment
            {
                Text = text,
                StartSecond = start,
                EndSecond = end
            });
        }

        return segments;
    }

    private static float TryReadFloat(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return (float)value.GetDouble();
        }

        return 0f;
    }

    private static void AddRawCortecsPassthrough(MultipartFormDataContent form, JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (property.NameEquals("file") || property.NameEquals("model"))
                continue;

            var value = ToMultipartValue(property.Value);
            if (value is null)
                continue;

            form.Add(new StringContent(value, Encoding.UTF8), property.Name);
        }
    }

    private static string? ToMultipartValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => value.GetRawText(),
            JsonValueKind.Object => value.GetRawText(),
            _ => value.GetRawText()
        };
}

