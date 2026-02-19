using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Bineric;

public partial class BinericProvider
{
    private static readonly JsonSerializerOptions SpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<SpeechResponse> SpeechRequestCore(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var metadata = request.GetProviderMetadata<BinericSpeechProviderMetadata>(GetIdentifier());
        var warnings = new List<object>();
        var now = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.OutputFormat)
            && !string.Equals(request.OutputFormat.Trim(), "mp3", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.OutputFormat.Trim(), "mpeg", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "unsupported", feature = "outputFormat", details = request.OutputFormat });
        }

        var language = (request.Language ?? metadata?.Language)?.Trim();
        var voice = (request.Voice ?? metadata?.Voice)?.Trim();
        var pitch = metadata?.Pitch?.Trim();
        var volume = metadata?.Volume?.Trim();
        var rate = request.Speed is null ? metadata?.Rate : request.Speed.Value;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = "text-to-speech",
            ["messages"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = request.Text
                }
            },
            ["options"] = new Dictionary<string, object?>
            {
                ["language"] = language,
                ["voice"] = voice,
                ["pitch"] = pitch,
                ["rate"] = rate,
                ["volume"] = volume
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/ai/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bineric TTS failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var audioContent = ExtractAudioContent(root);
        if (string.IsNullOrWhiteSpace(audioContent))
            throw new InvalidOperationException($"Bineric TTS returned no audio content. Body: {body}");

        var audioBase64 = StripDataUrlPrefix(audioContent);
        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new InvalidOperationException($"Bineric TTS returned empty audio data. Body: {body}");

        var responseFormat = ResolveResponseFormat(root);
        var mime = OpenAI.OpenAIProvider.MapToAudioMimeType(responseFormat);

        Dictionary<string, JsonElement>? providerMetadata = null;
        if (root.TryGetProperty("_bineric", out var binericMeta))
        {
            providerMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = binericMeta.Clone()
            };
        }

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = audioBase64,
                MimeType = mime,
                Format = responseFormat
            },
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static string? ExtractAudioContent(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("audioContent", out var audioContent)
                && audioContent.ValueKind == JsonValueKind.String)
            {
                return audioContent.GetString();
            }
        }

        return null;
    }

    private static string StripDataUrlPrefix(string value)
    {
        var trimmed = value.Trim();
        var marker = "base64,";
        var idx = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return trimmed[(idx + marker.Length)..];

        return trimmed;
    }

    private static string ResolveResponseFormat(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("format", out var format)
                && format.ValueKind == JsonValueKind.String)
            {
                return (format.GetString() ?? "mp3").Trim().ToLowerInvariant();
            }
        }

        return "mp3";
    }

    private sealed class BinericSpeechProviderMetadata
    {
        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("voice")]
        public string? Voice { get; set; }

        [JsonPropertyName("pitch")]
        public string? Pitch { get; set; }

        [JsonPropertyName("rate")]
        public float? Rate { get; set; }

        [JsonPropertyName("volume")]
        public string? Volume { get; set; }
    }
}

