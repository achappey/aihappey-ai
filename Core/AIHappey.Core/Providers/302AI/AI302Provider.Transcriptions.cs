using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AI302;

public partial class AI302Provider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var metadata = GetTranscriptionProviderMetadata<AI302TranscriptionProviderMetadata>(request, GetIdentifier());

        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        if (!string.IsNullOrWhiteSpace(metadata?.ResponseFormat))
            form.Add(new StringContent(metadata.ResponseFormat), "response_format");

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            form.Add(new StringContent(metadata.Language), "language");

        using var resp = await _client.PostAsync("302/v1/audio/transcriptions", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"302.AI STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, request.Model, now, warnings);
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(
        string json,
        string model,
        DateTime now,
        IEnumerable<object> warnings)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("segments", out var segmentsEl) &&
            segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segmentsEl.EnumerateArray())
            {
                var segmentText = seg.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                    ? textEl.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(segmentText))
                    continue;

                segments.Add(new TranscriptionSegment
                {
                    Text = segmentText,
                    StartSecond = seg.TryGetProperty("start", out var startEl)
                        && startEl.ValueKind == JsonValueKind.Number
                        ? (float)startEl.GetDouble()
                        : 0f,
                    EndSecond = seg.TryGetProperty("end", out var endEl)
                        && endEl.ValueKind == JsonValueKind.Number
                        ? (float)endEl.GetDouble()
                        : 0f
                });
            }
        }

        var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty
            : string.Join(" ", segments.Select(a => a.Text));

        return new TranscriptionResponse
        {
            Text = text,
            Language = root.TryGetProperty("language", out var languageEl)
                && languageEl.ValueKind == JsonValueKind.String
                ? languageEl.GetString()
                : null,
            DurationInSeconds = root.TryGetProperty("duration", out var durationEl)
                && durationEl.ValueKind == JsonValueKind.Number
                ? (float)durationEl.GetDouble()
                : null,
            Segments = segments,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = model,
                Body = json
            }
        };
    }

    private static T? GetTranscriptionProviderMetadata<T>(TranscriptionRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return default;

        if (!request.ProviderOptions.TryGetValue(providerId, out var element))
            return default;

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private sealed class AI302TranscriptionProviderMetadata
    {
        [JsonPropertyName("response_format")]
        public string? ResponseFormat { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }
    }
}
