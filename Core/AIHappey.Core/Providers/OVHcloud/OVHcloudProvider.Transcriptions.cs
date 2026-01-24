using AIHappey.Core.AI;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OVHcloud;

public partial class OVHcloudProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var metadata = request.GetProviderMetadata<OpenAiTranscriptionProviderMetadata>(GetIdentifier());

        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);

        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            form.Add(new StringContent(metadata.Language), "language");

        if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
            form.Add(new StringContent(metadata.Prompt), "prompt");

        if (metadata?.Temperature is not null)
        {
            form.Add(
                new StringContent(metadata.Temperature.Value.ToString(CultureInfo.InvariantCulture)),
                "temperature"
            );
        }

        var wantsTimestamps = metadata?.TimestampGranularities is not null;
        if (wantsTimestamps)
        {
            var granularities = metadata?.TimestampGranularities?
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];

            if (granularities.Length > 0)
            {
                foreach (var g in granularities)
                {
                    form.Add(new StringContent(g), "timestamp_granularities[]");
                }
            }
        }

        if (request.ProviderOptions is not null &&
            request.ProviderOptions.TryGetValue("diarize", out var diarizeEl) &&
            diarizeEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            form.Add(new StringContent(diarizeEl.GetBoolean() ? "true" : "false"), "diarize");
        }

        string? responseFormat = null;
        if (request.ProviderOptions is not null &&
            request.ProviderOptions.TryGetValue("response_format", out var responseFormatEl) &&
            responseFormatEl.ValueKind == JsonValueKind.String)
        {
            responseFormat = responseFormatEl.GetString();
        }

        if (wantsTimestamps && !string.Equals(responseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase))
            responseFormat = "verbose_json";

        if (!string.IsNullOrWhiteSpace(responseFormat))
            form.Add(new StringContent(responseFormat), "response_format");

        var url = "v1/audio/transcriptions";
        using var resp = await _client.PostAsync(url, form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"OVHcloud STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertOvhResponse(json, request.Model);
    }

    private static TranscriptionResponse ConvertOvhResponse(string json, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("segments", out var segmentsEl) &&
            segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segmentsEl.EnumerateArray())
            {
                var text = seg.TryGetProperty("text", out var textEl)
                    ? textEl.GetString() ?? string.Empty
                    : string.Empty;

                segments.Add(new TranscriptionSegment
                {
                    Text = text,
                    StartSecond = seg.TryGetProperty("start", out var startEl)
                        ? (float)startEl.GetDouble()
                        : 0f,
                    EndSecond = seg.TryGetProperty("end", out var endEl)
                        ? (float)endEl.GetDouble()
                        : 0f
                });
            }
        }

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? string.Empty
                : string.Join(" ", segments.Select(a => a.Text)),
            Language = root.TryGetProperty("language", out var lang)
                ? lang.GetString() ?? null
                : null,
            DurationInSeconds = root.TryGetProperty("duration", out var durationEl) &&
                durationEl.ValueKind == JsonValueKind.Number
                ? (float)durationEl.GetDouble()
                : null,
            Segments = segments,
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = json
            }
        };
    }
}
