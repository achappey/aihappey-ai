using AIHappey.Core.AI;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Fireworks;
using System.Globalization;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Fireworks;

public partial class FireworksProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
       TranscriptionRequest request,
       CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = request.GetProviderMetadata<FireworksTranscriptionProviderMetadata>(GetIdentifier());

        // Fireworks docs: model = whisper-v3 or whisper-v3-turbo
        var baseUrl = request.Model.Equals("whisper-v3-turbo", StringComparison.OrdinalIgnoreCase)
            ? "https://audio-turbo.api.fireworks.ai"
            : "https://audio-prod.api.fireworks.ai";

        var url = $"{baseUrl}/v1/audio/transcriptions";

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);

        using var form = new MultipartFormDataContent();

        // file (required)
        var fileName = "audio" + request.MediaType.GetAudioExtension(); // make sure this becomes .mp3/.wav/.flac etc
        var file = new ByteArrayContent(bytes);

        // Fireworks is fine with a per-part content-type
        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);

        // model (optional, but we send it)
        form.Add(new StringContent(request.Model), "model");

        // Always ask for verbose_json so you get segments back (your converter supports it)

        if (metadata?.Diarize == true || metadata?.TimestampGranularities?.Any() == true)
            form.Add(new StringContent("verbose_json"), "response_format");

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            form.Add(new StringContent(metadata.Language), "language");

        if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
            form.Add(new StringContent(metadata.Prompt), "prompt");

        // temperature (optional)
        if (metadata?.Temperature is not null)
        {
            form.Add(
                new StringContent(
                    metadata.Temperature.Value.ToString(CultureInfo.InvariantCulture)
                ),
                "temperature"
            );
        }

        if (!string.IsNullOrWhiteSpace(metadata?.Preprocessing))
            form.Add(new StringContent("preprocessing"), metadata.Preprocessing);

        if (!string.IsNullOrWhiteSpace(metadata?.VadModel))
            form.Add(new StringContent("vad_model"), metadata.VadModel);

        if (!string.IsNullOrWhiteSpace(metadata?.AlignmentModel))
            form.Add(new StringContent("alignment_model"), metadata.AlignmentModel);

        if (metadata?.TimestampGranularities != null && metadata.TimestampGranularities.Any())
        {
            foreach (var g in metadata.TimestampGranularities)
            {
                form.Add(new StringContent(g), "timestamp_granularities[]");
            }
        }

        // If diarize is requested, Fireworks requires verbose_json + word timestamps
        if (metadata?.Diarize == true)
        {
            form.Add(new StringContent("true"), "diarize");

            if (metadata?.MinSpeakers is int min)
                form.Add(new StringContent(min.ToString(CultureInfo.InvariantCulture)), "min_speakers");

            if (metadata?.MaxSpeakers is int max)
                form.Add(new StringContent(max.ToString(CultureInfo.InvariantCulture)), "max_speakers");
        }

        using var resp = await _client.PostAsync(url, form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Fireworks STT failed ({(int)resp.StatusCode}): {json}");

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
                    Text = seg.GetProperty("text").GetString() ?? "",
                    StartSecond = (float)seg.GetProperty("start").GetDouble(),
                    EndSecond = (float)seg.GetProperty("end").GetDouble()
                })
                .ToList()
            : [];

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : string.Join(" ", segments.Select(a => a.Text)),

            Segments = segments,

            Language = root.TryGetProperty("language", out var lang)
                ? lang.GetString()
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
