using System.Text.Json;
using AIHappey.Common.Model;
using System.Net.Http.Headers;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Groq;
using System.Globalization;

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
        var metadata = request.GetTranscriptionProviderMetadata<GroqTranscriptionProviderMetadata>(GetIdentifier());
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

        if (metadata?.TimestampGranularities != null && metadata.TimestampGranularities.Any())
        {
            foreach (var g in metadata.TimestampGranularities)
            {
                form.Add(new StringContent(g), "timestamp_granularities[]");
            }

            form.Add(new StringContent("verbose_json"), "response_format");
        }

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

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
