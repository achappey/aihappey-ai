using AIHappey.Core.AI;
using AIHappey.Common.Model;
using OpenAI.Audio;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider : IModelProvider
{
    public async Task<TranscriptionResponse> TranscribeWithDiarization(
        TranscriptionRequest request,
        CancellationToken ct = default)
    {
        var bytes = Convert.FromBase64String(request.Audio.ToString()!);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GetKey());

        using var form = new MultipartFormDataContent();

        // audio file
        var audioContent = new ByteArrayContent(bytes);
        audioContent.Headers.ContentType =
            new MediaTypeHeaderValue(request.MediaType);

        form.Add(audioContent, "file", "audio" + request.MediaType.GetAudioExtension());

        // REQUIRED diarization fields
        form.Add(new StringContent(request.Model), "model");
        form.Add(new StringContent("diarized_json"), "response_format");
        form.Add(new StringContent("auto"), "chunking_strategy");

        // optional known speakers
        // form.Add(new StringContent("agent"), "known_speaker_names[]");
        // form.Add(new StringContent("data:audio/wav;base64,AAA..."),
        //          "known_speaker_references[]");

        using var resp = await http.PostAsync(
            "https://api.openai.com/v1/audio/transcriptions",
            form,
            ct);

        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);

        return ConvertDiarizedJson(json, request.Model);
    }

    private static TranscriptionResponse ConvertDiarizedJson(
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
            Response = new ()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = json
            }
        };
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Model == "gpt-4o-transcribe-diarize")
            return await TranscribeWithDiarization(request, cancellationToken);

        var audioClient = new AudioClient(
            request.Model,
            GetKey()
        );

        var now = DateTime.UtcNow;
        List<string> results = [];
        List<object> warnings = [];
        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        using var memStream = new MemoryStream(bytes, writable: false);

        var result = await audioClient.TranscribeAudioAsync(memStream,
            "audio" + request.MediaType.GetAudioExtension(),
            new AudioTranscriptionOptions()
            {
            },
            cancellationToken);

        return new TranscriptionResponse()
        {
            Text = result.Value.Text,
            Segments = result.Value.Segments.Select(a => new TranscriptionSegment()
            {
                Text = a.Text,
                StartSecond = (float)a.StartTime.TotalSeconds,
                EndSecond = (float)a.EndTime.TotalSeconds
            }),
            Response = new ()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = result.GetRawResponse().Content.ToString(),
            },
            Language = result.Value.Language,
            DurationInSeconds = result.Value.Duration.HasValue
                ? (float)result.Value.Duration.Value.TotalSeconds : null
        };
    }
}