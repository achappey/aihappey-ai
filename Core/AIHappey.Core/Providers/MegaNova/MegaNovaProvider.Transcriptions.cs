using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;

namespace AIHappey.Core.Providers.MegaNova;

public partial class MegaNovaProvider
{
    private async Task<TranscriptionResponse> TranscriptionRequestMegaNova(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audioBase64 = ReadMegaNovaAudioBase64(request);
        var bytes = Convert.FromBase64String(audioBase64);
        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = GetMegaNovaProviderMetadata(request, GetIdentifier());
        var fileName = TryGetMegaNovaString(metadata, "filename", "fileName")
            ?? "audio" + GetMegaNovaAudioExtension(request.MediaType);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model.Trim()), "model");
        MergeMegaNovaProviderMetadata(form, metadata);

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MegaNova transcription failed ({(int)response.StatusCode}): {raw}");

        return ConvertMegaNovaTranscriptionResponse(raw, request.Model, now, warnings, response.GetHeaders(), form);
    }

    private TranscriptionResponse ConvertMegaNovaTranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        IEnumerable<object> warnings,
        Dictionary<string, string> headers,
        MultipartFormDataContent form)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var segments = ExtractMegaNovaTranscriptionSegments(root);

        var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString() ?? string.Empty
            : string.Join(" ", segments.Select(segment => segment.Text));

        return new TranscriptionResponse
        {
            Text = text,
            Language = TryGetMegaNovaString(root, "language"),
            DurationInSeconds = TryGetMegaNovaFloat(root, "duration", "duration_in_seconds", "durationInSeconds"),
            Segments = segments,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new ResponseData
            {
                Timestamp = timestamp,
                Headers = headers,
                ModelId = model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    private static List<TranscriptionSegment> ExtractMegaNovaTranscriptionSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segmentsEl) || segmentsEl.ValueKind != JsonValueKind.Array)
            return [];

        var segments = new List<TranscriptionSegment>();
        foreach (var segment in segmentsEl.EnumerateArray())
        {
            var text = TryGetMegaNovaString(segment, "text") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var start = TryGetMegaNovaFloat(segment, "start", "start_second", "startSecond") ?? 0f;
            var end = TryGetMegaNovaFloat(segment, "end", "end_second", "endSecond") ?? start;

            segments.Add(new TranscriptionSegment
            {
                Text = text,
                StartSecond = start,
                EndSecond = end < start ? start : end
            });
        }

        return segments;
    }

}
