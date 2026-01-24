using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.NLPCloud;
using AIHappey.Core.AI;
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

        var model = request.Model.Trim();
        if (!string.Equals(model, "whisper/asr", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("NLPCloud transcription only supports the whisper/asr model.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var encoded = request.Audio.ToString() ?? string.Empty;
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var base64Index = encoded.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
            if (base64Index >= 0)
                encoded = encoded[(base64Index + "base64,".Length)..];
        }

        if (string.IsNullOrWhiteSpace(encoded))
            throw new ArgumentException("Audio base64 payload is empty.", nameof(request));

        var metadata = request.GetProviderMetadata<NLPCloudTranscriptionProviderMetadata>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["encoded_file"] = encoded
        };

        if (!string.IsNullOrWhiteSpace(metadata?.InputLanguage))
            payload["input_language"] = metadata.InputLanguage;

        if (request.ProviderOptions is not null)
        {
            if (request.ProviderOptions.ContainsKey("url"))
                warnings.Add(new { type = "unsupported", feature = "url" });
            if (request.ProviderOptions.ContainsKey("encoded_file"))
                warnings.Add(new { type = "ignored", feature = "encoded_file", reason = "Request audio is used instead." });
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"gpu/{model}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, TranscriptionJson),
                Encoding.UTF8,
                "application/json")
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"NLPCloud transcription failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, model, now, warnings);
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(
        string json,
        string model,
        DateTime now,
        IEnumerable<object> warnings)
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
            Response = new()
            {
                Timestamp = now,
                ModelId = model,
                Body = json
            }
        };
    }
}
