using AIHappey.Core.AI;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Mistral;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var metadata = request.GetProviderMetadata<MistralTranscriptionProviderMetadata>(GetIdentifier());
        using var form = new MultipartFormDataContent();

        // audio file
        var audioContent = new ByteArrayContent(bytes);
        audioContent.Headers.ContentType =
            new MediaTypeHeaderValue(request.MediaType);

        form.Add(audioContent, "file", "audio" + request.MediaType.GetAudioExtension());

        // REQUIRED diarization fields
        form.Add(new StringContent(request.Model), "model");

        if (metadata?.TimestampGranularities != null && metadata.TimestampGranularities.Any())
        {
            foreach (var g in metadata.TimestampGranularities)
            {
                form.Add(new StringContent(g), "timestamp_granularities[]");
            }
        }

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            form.Add(new StringContent(metadata.Language), "language");

        using var resp = await _client.PostAsync(
            "https://api.mistral.ai/v1/audio/transcriptions",
            form,
            cancellationToken);

        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        return ConvertTranscriptionResponse(json, request.Model, GetIdentifier(), resp.GetHeaders());
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(
       string json,
       string model,
       string providerId,
       IDictionary<string, string>? headers = null)
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
            Language = root.TryGetProperty("language", out var languageEl)
                    ? languageEl.GetString() ?? null : null,
            ProviderMetadata = providerId.CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = root.TryGetProperty("model", out var modelEl)
                    ? modelEl.GetString()?.ToModelId(providerId)
                        ?? model.ToModelId(providerId) : model.ToModelId(providerId),
                Body = root.Clone()
            }
        };
    }


}
