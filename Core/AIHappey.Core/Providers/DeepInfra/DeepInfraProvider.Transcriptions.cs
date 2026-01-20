using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.DeepInfra;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Incoming pipeline passes base64 (data-url prefix already stripped upstream).
        var bytes = Convert.FromBase64String(request.Audio.ToString()!);

        var metadata = request.GetTranscriptionProviderMetadata<DeepInfraTranscriptionProviderMetadata>(GetIdentifier());

        using var form = new MultipartFormDataContent();

        // DeepInfra expects form field name: "audio".
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "audio", fileName);

        // Optional DeepInfra fields
        if (!string.IsNullOrWhiteSpace(metadata?.Task))
            form.Add(new StringContent(metadata.Task), "task");

        if (!string.IsNullOrWhiteSpace(metadata?.InitialPrompt))
            form.Add(new StringContent(metadata.InitialPrompt), "initial_prompt");

        if (metadata?.Temperature is not null)
            form.Add(new StringContent(metadata.Temperature.Value.ToString(CultureInfo.InvariantCulture)), "temperature");

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            form.Add(new StringContent(metadata.Language), "language");

        if (!string.IsNullOrWhiteSpace(metadata?.ChunkLevel))
            form.Add(new StringContent(metadata.ChunkLevel), "chunk_level");

        if (metadata?.ChunkLengthSeconds is not null)
        {
            var v = metadata.ChunkLengthSeconds.Value;
            if (v is < 1 or > 30)
                warnings.Add(new { type = "invalid", feature = "chunk_length_s", reason = "Must be in range 1..30." });
            else
                form.Add(new StringContent(v.ToString(CultureInfo.InvariantCulture)), "chunk_length_s");
        }

        using var resp = await _client.PostAsync($"v1/inference/{request.Model}", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepInfra STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertDeepInfraTranscriptionResponse(json, request.Model, now, warnings);
    }

    private static TranscriptionResponse ConvertDeepInfraTranscriptionResponse(
        string json,
        string model,
        DateTime now,
        IEnumerable<object> warnings)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("segments", out var segsEl) && segsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segsEl.EnumerateArray())
            {
                var text = seg.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                    ? (t.GetString() ?? "")
                    : "";

                var start = seg.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.Number
                    ? (float)s.GetDouble()
                    : 0;

                var end = seg.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.Number
                    ? (float)e.GetDouble()
                    : 0;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = text,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }
        }

        var textOut = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? (textEl.GetString() ?? "")
            : string.Join(" ", segments.Select(a => a.Text));

        var language = root.TryGetProperty("language", out var langEl) && langEl.ValueKind == JsonValueKind.String
            ? langEl.GetString()
            : null;

        float? duration = null;
        if (root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
            duration = (float)durEl.GetDouble();

        return new TranscriptionResponse
        {
            Text = textOut,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = model,
                Body = json
            }
        };
    }
}

