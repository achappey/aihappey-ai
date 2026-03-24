using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Tensorix;

public partial class TensorixProvider
{
    private const string TensorixTranscriptionsEndpoint = "v1/audio/transcriptions";

    private async Task<TranscriptionResponse> TranscriptionRequestCore(
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
        var warnings = new List<object>();

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        string? appliedLanguage = null;
        string? appliedResponseFormat = null;
        List<string> appliedTimestampGranularities = [];

        var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (providerOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
            {
                if (property.NameEquals("language") && property.Value.ValueKind == JsonValueKind.String)
                {
                    var language = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(language))
                    {
                        appliedLanguage = language;
                        form.Add(new StringContent(language), "language");
                    }

                    continue;
                }

                if (property.NameEquals("response_format") && property.Value.ValueKind == JsonValueKind.String)
                {
                    var responseFormat = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(responseFormat))
                    {
                        appliedResponseFormat = responseFormat;
                        form.Add(new StringContent(responseFormat), "response_format");
                    }

                    continue;
                }

                if (property.NameEquals("timestamp_granularities") && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var granularityElement in property.Value.EnumerateArray())
                    {
                        if (granularityElement.ValueKind != JsonValueKind.String)
                            continue;

                        var granularity = granularityElement.GetString();
                        if (string.IsNullOrWhiteSpace(granularity))
                            continue;

                        appliedTimestampGranularities.Add(granularity);
                        form.Add(new StringContent(granularity), "timestamp_granularities[]");
                    }

                    continue;
                }

                warnings.Add(new { type = "unsupported", feature = property.Name });
            }
        }

        using var resp = await _client.PostAsync(TensorixTranscriptionsEndpoint, form, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Tensorix transcription request failed ({(int)resp.StatusCode}): {raw}");

        string text;
        string? languageFromResponse = null;
        float? duration = null;
        List<TranscriptionSegment> segments = [];
        object responseBody;
        string modelId = request.Model;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                ? textEl.GetString() ?? string.Empty
                : string.Empty;

            languageFromResponse = root.TryGetProperty("language", out var languageEl) && languageEl.ValueKind == JsonValueKind.String
                ? languageEl.GetString()
                : null;

            if (root.TryGetProperty("duration", out var durationEl) && durationEl.ValueKind == JsonValueKind.Number)
                duration = (float)durationEl.GetDouble();

            if (root.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var segment in segmentsEl.EnumerateArray())
                {
                    if (segment.ValueKind != JsonValueKind.Object)
                        continue;

                    var segmentText = segment.TryGetProperty("text", out var segmentTextEl) && segmentTextEl.ValueKind == JsonValueKind.String
                        ? segmentTextEl.GetString() ?? string.Empty
                        : string.Empty;

                    var start = TryReadFloat(segment, "start", "start_second", "startSecond");
                    var end = TryReadFloat(segment, "end", "end_second", "endSecond");

                    if (end < start)
                        end = start;

                    segments.Add(new TranscriptionSegment
                    {
                        Text = segmentText,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }

            if (string.IsNullOrWhiteSpace(text))
                text = string.Join(" ", segments.Select(s => s.Text));

            if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                modelId = modelEl.GetString() ?? request.Model;

            responseBody = JsonSerializer.Deserialize<object>(raw, JsonSerializerOptions.Web) ?? raw;
        }
        catch (JsonException)
        {
            text = raw;
            responseBody = raw;
        }

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                endpoint = TensorixTranscriptionsEndpoint,
                model = request.Model,
                language = appliedLanguage,
                response_format = appliedResponseFormat,
                timestamp_granularities = appliedTimestampGranularities
            })
        };

        return new TranscriptionResponse
        {
            ProviderMetadata = providerMetadata,
            Text = text,
            Language = languageFromResponse,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = modelId,
                Body = responseBody
            }
        };
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
}
