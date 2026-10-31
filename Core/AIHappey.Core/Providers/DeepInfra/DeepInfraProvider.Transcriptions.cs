using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

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

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        using var form = new MultipartFormDataContent();

        // DeepInfra expects form field name: "audio".
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "audio", fileName);

        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            throw new JsonException("DeepInfra provider options must be a JSON object.");

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                switch (property.Name.ToLowerInvariant())
                {
                    case "task":
                        AddOptionalFormField(form, "task", property.Value.Deserialize<string?>());
                        break;
                    case "initial_prompt":
                        AddOptionalFormField(form, "initial_prompt", property.Value.Deserialize<string?>());
                        break;
                    case "temperature":
                        if (property.Value.Deserialize<float?>(JsonSerializerOptions.Web) is { } temperature)
                            form.Add(new StringContent(temperature.ToString(CultureInfo.InvariantCulture)), "temperature");
                        break;
                    case "language":
                        AddOptionalFormField(form, "language", property.Value.Deserialize<string?>());
                        break;
                    case "chunk_level":
                        AddOptionalFormField(form, "chunk_level", property.Value.Deserialize<string?>());
                        break;
                    case "chunk_length_s":
                        if (property.Value.Deserialize<int?>(JsonSerializerOptions.Web) is { } chunkLengthSeconds)
                        {
                            if (chunkLengthSeconds is < 1 or > 30)
                                warnings.Add(new { type = "invalid", feature = "chunk_length_s", reason = "Must be in range 1..30." });
                            else
                                form.Add(new StringContent(chunkLengthSeconds.ToString(CultureInfo.InvariantCulture)), "chunk_length_s");
                        }
                        break;
                    default:
                        form.Add(new StringContent(GetDeepInfraFormValue(property.Value)), property.Name);
                        break;
                }
            }
        }

        using var resp = await _client.PostAsync($"v1/inference/{request.Model}", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepInfra STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertDeepInfraTranscriptionResponse(json, request.Model, now, warnings);
    }

    private static void AddOptionalFormField(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value), name);
    }

    private static string GetDeepInfraFormValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();

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
