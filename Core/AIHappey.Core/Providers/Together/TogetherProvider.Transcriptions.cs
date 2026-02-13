using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider 
{

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? "openai/whisper-large-v3"
            : request.Model;

        // IMPORTANT: Together diarization metadata is returned in verbose_json (+ word timestamps).
        // If you send diarize=true with response_format=json, Together often 500s.
        //    var diarize = request.Diarize == true;
        var diarize = false;
        var responseFormat = diarize ? "verbose_json" : "json";

        var fileName = "audio" + request.MediaType.GetAudioExtension();

        using var form = new MultipartFormDataContent
        {
            // file part: keep it CURL-like (no per-part Content-Type header)
            { new ByteArrayContent(bytes), "file", fileName },

            // minimal required
            { new StringContent(model), "model" },

            // optional (safe)
       //     { new StringContent(string.IsNullOrWhiteSpace(request.Language) ? "auto" : request.Language), "language" },
            { new StringContent("auto"), "language" },
            { new StringContent(responseFormat), "response_format" },
            //{ new StringContent("0"), "temperature" }
        };

        // diarization MUST be paired with verbose_json (+ word timestamps)
        if (diarize)
        {
            form.Add(new StringContent("true"), "diarize");

            // Send as repeated fields (most form-data parsers accept this)
            form.Add(new StringContent("word"), "timestamp_granularities");
            form.Add(new StringContent("segment"), "timestamp_granularities");

            /*     if (request.MinSpeakers is int min)
                     form.Add(new StringContent(min.ToString(CultureInfo.InvariantCulture)), "min_speakers");

                 if (request.MaxSpeakers is int max)
                     form.Add(new StringContent(max.ToString(CultureInfo.InvariantCulture)), "max_speakers");*/
        }


        using var resp = await _client.PostAsync(
            "https://api.together.xyz/v1/audio/transcriptions",
            form,
            cancellationToken);

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            throw new Exception(json);
        }



        return ConvertTranscriptionResponse(json, request.Model);
    }



    private static TranscriptionResponse ConvertTranscriptionResponse(
        string json,
        string model)
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
