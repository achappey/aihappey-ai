using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.APIpie;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.APIpie;

public partial class APIpieProvider
{
    private const string ApiPieTranscriptionEndpoint = "v1/audio/transcriptions";

    public async Task<TranscriptionResponse> TranscriptionRequest(
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
        var metadata = request.GetProviderMetadata<APIpieTranscriptionProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
            form.Add(new StringContent(metadata.Prompt), "prompt");

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            form.Add(new StringContent(metadata.Language), "language");

        if (metadata?.Temperature is not null)
            form.Add(new StringContent(metadata.Temperature.Value.ToString(CultureInfo.InvariantCulture)), "temperature");

        var wantsTimestamps = metadata?.TimestampGranularities?.Any() == true;

        var responseFormat = metadata?.ResponseFormat;
        if (wantsTimestamps && !string.Equals(responseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase))
            responseFormat = "verbose_json";

        if (!string.IsNullOrWhiteSpace(responseFormat))
            form.Add(new StringContent(responseFormat), "response_format");

        if (wantsTimestamps)
        {
            foreach (var granularity in metadata!.TimestampGranularities!.Where(g => !string.IsNullOrWhiteSpace(g)))
                form.Add(new StringContent(granularity), "timestamp_granularities");
        }

        using var resp = await _client.PostAsync(ApiPieTranscriptionEndpoint, form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"APIpie STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, request.Model, now);
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(string json, string model, DateTime timestamp)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentsEl.EnumerateArray())
            {
                var text = segment.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                    ? textEl.GetString() ?? string.Empty
                    : string.Empty;

                var start = TryReadFloat(segment, "start", "start_second", "startSecond");
                var end = TryReadFloat(segment, "end", "end_second", "endSecond");

                if (end < start)
                    end = start;

                segments.Add(new TranscriptionSegment
                {
                    Text = text,
                    StartSecond = start,
                    EndSecond = end
                });
            }
        }

        var textValue = root.TryGetProperty("text", out var textRootEl) && textRootEl.ValueKind == JsonValueKind.String
            ? textRootEl.GetString() ?? string.Empty
            : string.Join(" ", segments.Select(a => a.Text));

        var language = root.TryGetProperty("language", out var languageEl) && languageEl.ValueKind == JsonValueKind.String
            ? languageEl.GetString()
            : null;

        float? duration = null;

        if (root.TryGetProperty("duration", out var durationEl) && durationEl.ValueKind == JsonValueKind.Number)
            duration = (float)durationEl.GetDouble();

        if (!duration.HasValue && root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            if (usageEl.TryGetProperty("seconds", out var secondsEl) && secondsEl.ValueKind == JsonValueKind.Number)
                duration = (float)secondsEl.GetDouble();
        }

        return new TranscriptionResponse
        {
            Text = textValue,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                    ? modelEl.GetString() ?? model
                    : model,
                Body = json
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

