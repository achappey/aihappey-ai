using System.Text.Json;
using AIHappey.Common.Model;
using System.Net.Http.Headers;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider : IModelProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? "whisper-large-v3"
            : request.Model;

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);

        using var form = new MultipartFormDataContent();

        // file (Groq accepts per-part Content-Type)
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);

        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);

        // required
        form.Add(new StringContent(model), "model");

        // optional
        /*       if (!string.IsNullOrWhiteSpace(request.Language))
                   form.Add(new StringContent(request.Language), "language");

               if (!string.IsNullOrWhiteSpace(request.Prompt))
                   form.Add(new StringContent(request.Prompt), "prompt");*/

        // ask for verbose_json so segments exist
        form.Add(new StringContent("json"), "response_format");

        /*   form.Add(new StringContent(
               request.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)),
               "temperature"
           );*/

        // timestamps only valid for verbose_json
      //  form.Add(new StringContent("segment"), "timestamp_granularities");

        using var resp = await _client.PostAsync(
            "https://api.groq.com/openai/v1/audio/transcriptions",
            form,
            cancellationToken
        );

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Groq STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, model);
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