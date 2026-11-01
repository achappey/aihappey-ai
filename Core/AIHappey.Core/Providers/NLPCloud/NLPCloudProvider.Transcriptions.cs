using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    private static readonly JsonSerializerOptions TranscriptionJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal async Task<TranscriptionResponse> TranscriptionRequestInternal(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var model = NormalizeNativeModel(request.Model);
        if (!string.Equals(model, "whisper/asr", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("NLPCloud transcription only supports the whisper/asr model.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var payload = new Dictionary<string, object?>();
        SetNativeAudio(payload, request.Audio);

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (property.Name.Equals("input_language", StringComparison.OrdinalIgnoreCase))
                {
                    var inputLanguage = property.Value.Deserialize<string?>(TranscriptionJson);
                    if (!string.IsNullOrWhiteSpace(inputLanguage))
                        payload["input_language"] = inputLanguage;
                }
                else if (!property.Name.Equals("encoded_file", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Equals("url", StringComparison.OrdinalIgnoreCase))
                {
                    payload[property.Name] = property.Value;
                }
            }
        }
        else if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            throw new JsonException("NLPCloud provider metadata must be a JSON object.");
        }

        if (metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("encoded_file", out _))
            warnings.Add(new { type = "ignored", feature = "encoded_file", reason = "Request audio is used instead." });
        if (metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("url", out _))
            warnings.Add(new { type = "ignored", feature = "url", reason = "Request audio is used instead." });

        var requestBody = JsonSerializer.Serialize(payload, TranscriptionJson);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"gpu/{model}")
        {
            Content = new StringContent(
                requestBody,
                Encoding.UTF8,
                "application/json")
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"NLPCloud transcription failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, model, now, warnings, requestBody);
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(
        string json,
        string model,
        DateTime now,
        IEnumerable<object> warnings,
        string requestBody)
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
                    Text = seg.TryGetProperty("text", out var textEl) ? (textEl.GetString() ?? "") : "",
                    StartSecond = seg.TryGetProperty("start", out var startEl) ? (float)startEl.GetDouble() : 0,
                    EndSecond = seg.TryGetProperty("end", out var endEl) ? (float)endEl.GetDouble() : 0,
                })
                .ToList()
            : [];

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : string.Join(" ", segments.Select(a => a.Text)),
            Language = root.TryGetProperty("language", out var lang) ? lang.GetString() : null,
            DurationInSeconds = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number
                ? (float)dur.GetDouble()
                : null,
            Segments = segments,
            Warnings = warnings,
            ProviderMetadata = new() { ["nlpcloud"] = root.Clone() },
            Response = new()
            {
                Timestamp = now,
                ModelId = model,
                Body = json
            },
            Request = new TranscriptionRequestItem
            {
                Body = requestBody
            }
        };
    }
}
