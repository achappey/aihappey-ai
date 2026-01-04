using AIHappey.Core.AI;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using System.Text.Json;

namespace AIHappey.Core.Providers.Fireworks;

public partial class FireworksProvider : IModelProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
       TranscriptionRequest request,
       CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

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

        // optional fields (safe)
        //    if (!string.IsNullOrWhiteSpace(request.Language))
        //       form.Add(new StringContent(request.Language), "language");

        // Always ask for verbose_json so you get segments back (your converter supports it)
        form.Add(new StringContent("verbose_json"), "response_format");

        // Default temp 0
        form.Add(new StringContent("0"), "temperature");

        // If diarize is requested, Fireworks requires verbose_json + word timestamps
        /*    if (request.Diarize == true)
            {
                form.Add(new StringContent("true"), "diarize");

                // repeat fields => works as list[string] in multipart
                form.Add(new StringContent("word"), "timestamp_granularities");
                form.Add(new StringContent("segment"), "timestamp_granularities");

                if (request.MinSpeakers is int min)
                    form.Add(new StringContent(min.ToString(System.Globalization.CultureInfo.InvariantCulture)), "min_speakers");

                if (request.MaxSpeakers is int max)
                    form.Add(new StringContent(max.ToString(System.Globalization.CultureInfo.InvariantCulture)), "max_speakers");
            }*/

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
                    StartSecond = seg.GetProperty("start").GetDouble(),
                    EndSecond = seg.GetProperty("end").GetDouble()
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

            Response = new ()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = json
            }
        };
    }

}