using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider : IModelProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);

        using var form = new MultipartFormDataContent();

        // audio file
        var audioContent = new ByteArrayContent(bytes);
        audioContent.Headers.ContentType =
            new MediaTypeHeaderValue(request.MediaType);

        form.Add(audioContent, "file", "audio" + request.MediaType.GetAudioExtension());

        // REQUIRED diarization fields
        form.Add(new StringContent(request.Model), "model");
        //form.Add(new StringContent("segment"), "timestamp_granularities");

        using var resp = await _client.PostAsync(
            "https://api.mistral.ai/v1/audio/transcriptions",
            form,
            cancellationToken);

        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

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
            Language = root.TryGetProperty("language", out var languageEl)
                    ? languageEl.GetString() ?? null : null,
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = root.TryGetProperty("model", out var modelEl)
                    ? modelEl.GetString() ?? model : model,
                Body = json
            }
        };
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}