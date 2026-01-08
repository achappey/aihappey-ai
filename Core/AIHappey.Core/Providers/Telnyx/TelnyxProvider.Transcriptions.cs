using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Telnyx;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Telnyx;

public partial class TelnyxProvider : IModelProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = request.GetTranscriptionProviderMetadata<TelnyxTranscriptionProviderMetadata>(GetIdentifier());

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var fileName = "audio" + request.MediaType.GetAudioExtension();

        using var form = new MultipartFormDataContent();

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
                "temperature");
        }

        var wantsTimestamps = metadata?.TimestampGranularities?.Any() == true;

        var responseFormat = metadata?.ResponseFormat;
        if (wantsTimestamps && !string.Equals(responseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase))
            responseFormat = "verbose_json";

        if (!string.IsNullOrWhiteSpace(responseFormat))
            form.Add(new StringContent(responseFormat!), "response_format");

        if (wantsTimestamps)
        {
            foreach (var g in metadata!.TimestampGranularities!)
                form.Add(new StringContent(g), "timestamp_granularities[]");
        }

        using var resp = await _client.PostAsync("ai/audio/transcriptions", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Telnyx STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, request.Model);
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(string json, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segmentsEl = root.TryGetProperty("segments", out var s) ? s : default;
        var segments = segmentsEl.ValueKind == JsonValueKind.Array
            ? segmentsEl.EnumerateArray()
                .Select(seg => new TranscriptionSegment
                {
                    Text = seg.TryGetProperty("text", out var textEl) ? (textEl.GetString() ?? "") : "",
                    StartSecond = seg.TryGetProperty("start", out var startEl) ? (float)startEl.GetDouble() : 0,
                    EndSecond = seg.TryGetProperty("end", out var endEl) ? (float)endEl.GetDouble() : 0,
                })
                .ToList()
            : [];

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : string.Join(" ", segments.Select(a => a.Text)),
            Segments = segments,
            Language = root.TryGetProperty("language", out var lang) ? lang.GetString() : null,
            DurationInSeconds = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number
                ? (float)dur.GetDouble()
                : null,
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = json
            }
        };
    }
}

