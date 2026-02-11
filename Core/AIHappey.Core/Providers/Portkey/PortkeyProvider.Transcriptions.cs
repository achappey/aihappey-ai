using AIHappey.Core.AI;
using System.Text.Json;
using System.Globalization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Portkey;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Portkey;

public partial class PortkeyProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var audioString = request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        var bytes = Convert.FromBase64String(audioString);
        var metadata = request.GetProviderMetadata<PortkeyTranscriptionProviderMetadata>(GetIdentifier());

        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        form.Add(new ByteArrayContent(bytes), "file", fileName);

        form.Add(new StringContent(request.Model), "model");

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            form.Add(new StringContent(metadata.Language), "language");

        if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
            form.Add(new StringContent(metadata.Prompt), "prompt");

        var responseFormat = string.IsNullOrWhiteSpace(metadata?.ResponseFormat)
            ? null
            : metadata.ResponseFormat.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(responseFormat))
            form.Add(new StringContent(responseFormat), "response_format");

        if (metadata?.Temperature is not null)
        {
            form.Add(
                new StringContent(metadata.Temperature.Value.ToString(CultureInfo.InvariantCulture)),
                "temperature");
        }

        if (metadata?.TimestampGranularities is not null)
        {
            foreach (var granularity in metadata.TimestampGranularities.Where(g => !string.IsNullOrWhiteSpace(g)))
                form.Add(new StringContent(granularity), "timestamp_granularities[]");
        }

        using var resp = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Portkey STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, request.Model);
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(string json, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segmentsEl = root.TryGetProperty("segments", out var s)
            ? s
            : default;

        var segments = segmentsEl.ValueKind == JsonValueKind.Array
            ? segmentsEl.EnumerateArray()
                .Select(seg => new TranscriptionSegment
                {
                    Text = seg.TryGetProperty("text", out var textEl)
                        ? textEl.GetString() ?? ""
                        : "",
                    StartSecond = seg.TryGetProperty("start", out var startEl) && startEl.TryGetDouble(out var start)
                        ? (float)start
                        : 0f,
                    EndSecond = seg.TryGetProperty("end", out var endEl) && endEl.TryGetDouble(out var end)
                        ? (float)end
                        : 0f
                })
                .ToList()
            : [];

        float? durationInSeconds = null;
        if (root.TryGetProperty("duration", out var durationEl))
        {
            if (durationEl.ValueKind == JsonValueKind.Number && durationEl.TryGetDouble(out var durationNum))
                durationInSeconds = (float)durationNum;
            else if (durationEl.ValueKind == JsonValueKind.String
                && float.TryParse(durationEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var durationStr))
                durationInSeconds = durationStr;
        }

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : string.Join(" ", segments.Select(a => a.Text)),

            Segments = segments,

            Language = root.TryGetProperty("language", out var lang)
                ? lang.GetString()
                : null,

            DurationInSeconds = durationInSeconds,

            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = json
            }
        };
    }


}
