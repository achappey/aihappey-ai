using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.ElevenLabs;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.ElevenLabs;

public partial class ElevenLabsProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = request.GetProviderMetadata<ElevenLabsTranscriptionProviderMetadata>(GetIdentifier());

        var modelId = !string.IsNullOrWhiteSpace(request.Model)
            ? request.Model
            : "scribe_v1";

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(modelId), "model_id");

        if (!string.IsNullOrWhiteSpace(metadata?.LanguageCode))
            form.Add(new StringContent(metadata.LanguageCode), "language_code");
        if (metadata?.TagAudioEvents is not null)
            form.Add(new StringContent(metadata.TagAudioEvents.Value.ToString().ToLowerInvariant()), "tag_audio_events");
        if (metadata?.NumSpeakers is not null)
            form.Add(new StringContent(metadata.NumSpeakers.Value.ToString()), "num_speakers");
        if (!string.IsNullOrWhiteSpace(metadata?.TimestampsGranularity))
            form.Add(new StringContent(metadata.TimestampsGranularity), "timestamps_granularity");
        if (metadata?.Diarize is not null)
            form.Add(new StringContent(metadata.Diarize.Value.ToString().ToLowerInvariant()), "diarize");
        if (metadata?.DiarizationThreshold is not null)
            form.Add(new StringContent(metadata.DiarizationThreshold.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "diarization_threshold");
        if (!string.IsNullOrWhiteSpace(metadata?.FileFormat))
            form.Add(new StringContent(metadata.FileFormat), "file_format");
        if (metadata?.Temperature is not null)
            form.Add(new StringContent(metadata.Temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "temperature");
        if (metadata?.Seed is not null)
            form.Add(new StringContent(metadata.Seed.Value.ToString()), "seed");
        if (metadata?.UseMultiChannel is not null)
            form.Add(new StringContent(metadata.UseMultiChannel.Value.ToString().ToLowerInvariant()), "use_multi_channel");

        var query = new List<string>();
        if (metadata?.EnableLogging is not null)
            query.Add($"enable_logging={metadata.EnableLogging.Value.ToString().ToLowerInvariant()}");

        var url = "v1/speech-to-text" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        using var resp = await _client.PostAsync(url, form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ElevenLabs STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertResponse(json, modelId);
    }

    private static TranscriptionResponse ConvertResponse(string json, string modelId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
        var language = root.TryGetProperty("language_code", out var lang) ? lang.GetString() : null;

        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in wordsEl.EnumerateArray())
            {
                var wText = w.TryGetProperty("text", out var wt) ? (wt.GetString() ?? string.Empty) : string.Empty;
                var start = w.TryGetProperty("start", out var ws) ? (float)ws.GetDouble() : 0f;
                var end = w.TryGetProperty("end", out var we) ? (float)we.GetDouble() : start;
                if (!string.IsNullOrWhiteSpace(wText))
                    segments.Add(new TranscriptionSegment { Text = wText, StartSecond = start, EndSecond = end });
            }
        }

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            Segments = segments,
            Warnings = [],
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = modelId, Body = json }
        };
    }
}

