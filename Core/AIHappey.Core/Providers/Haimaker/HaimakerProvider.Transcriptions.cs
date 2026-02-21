using AIHappey.Core.AI;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIHappey.Core.Providers.Haimaker;

public partial class HaimakerProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? "openai/whisper-1"
            : request.Model;

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var metadata = request.GetProviderMetadata<OpenAiTranscriptionProviderMetadata>(GetIdentifier());

        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);

        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(model), "model");

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            form.Add(new StringContent(metadata.Language), "language");

        if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
            form.Add(new StringContent(metadata.Prompt), "prompt");

        if (metadata?.Temperature is not null)
        {
            form.Add(
                new StringContent(metadata.Temperature.Value.ToString(CultureInfo.InvariantCulture)),
                "temperature"
            );
        }

        if (metadata?.TimestampGranularities is not null)
        {
            var granularities = metadata.TimestampGranularities
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (granularities.Length > 0)
            {
                foreach (var g in granularities)
                    form.Add(new StringContent(g), "timestamp_granularities[]");

                form.Add(new StringContent("verbose_json"), "response_format");
            }
        }

        using var resp = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Haimaker transcription failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, model);
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(string json, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("segments", out var segmentsEl) &&
            segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segmentsEl.EnumerateArray())
            {
                segments.Add(new TranscriptionSegment
                {
                    Text = seg.TryGetProperty("text", out var textEl)
                        ? textEl.GetString() ?? string.Empty
                        : string.Empty,
                    StartSecond = seg.TryGetProperty("start", out var startEl)
                        ? (float)startEl.GetDouble()
                        : 0f,
                    EndSecond = seg.TryGetProperty("end", out var endEl)
                        ? (float)endEl.GetDouble()
                        : 0f
                });
            }
        }

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? string.Empty
                : string.Join(" ", segments.Select(a => a.Text)),
            Language = root.TryGetProperty("language", out var lang)
                ? lang.GetString()
                : null,
            Segments = segments,
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = json
            }
        };
    }
}
