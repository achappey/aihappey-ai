using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.AI302;

public partial class AI302Provider
{
    private static readonly JsonSerializerOptions AI302SpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var metadata = GetSpeechProviderMetadata<AI302SpeechProviderMetadata>(request, GetIdentifier());
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var voice = !string.IsNullOrWhiteSpace(request.Voice)
            ? request.Voice!.Trim()
            : metadata?.Voice?.Trim();

        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Voice is required for 302.AI speech endpoint.", nameof(request));

        var outputFormat = string.IsNullOrWhiteSpace(request.OutputFormat)
            ? (string.IsNullOrWhiteSpace(metadata?.ResponseFormat) ? "mp3" : metadata!.ResponseFormat!.Trim().ToLowerInvariant())
            : request.OutputFormat.Trim().ToLowerInvariant();

        var payload = new Dictionary<string, object?>
        {
            ["input"] = request.Text,
            ["model"] = request.Model,
            ["voice"] = voice,
            ["response_format"] = outputFormat
        };

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["instructions"] = request.Instructions;

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        if (!string.IsNullOrWhiteSpace(metadata?.StreamFormat))
            payload["stream_format"] = metadata.StreamFormat;

        if (metadata?.Volume is not null)
            payload["volume"] = metadata.Volume.Value;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "302/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, AI302SpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var contentBytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(contentBytes);
            throw new InvalidOperationException($"302.AI speech request failed ({(int)resp.StatusCode}): {err}");
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType;
        var isJson = contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

        if (isJson)
        {
            var raw = Encoding.UTF8.GetString(contentBytes);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var audioUrl = root.TryGetProperty("audio_url", out var audioUrlEl) && audioUrlEl.ValueKind == JsonValueKind.String
                ? audioUrlEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(audioUrl))
                throw new InvalidOperationException("302.AI speech response JSON did not include 'audio_url'.");

            var responseFormat = root.TryGetProperty("format", out var formatEl) && formatEl.ValueKind == JsonValueKind.String
                ? formatEl.GetString() ?? outputFormat
                : outputFormat;

            using var audioResp = await _client.GetAsync(audioUrl, cancellationToken);
            var audioBytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!audioResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"302.AI speech audio download failed ({(int)audioResp.StatusCode}).");

            var mime = audioResp.Content.Headers.ContentType?.MediaType
                ?? OpenAI.OpenAIProvider.MapToAudioMimeType(responseFormat);

            return new SpeechResponse
            {
                Audio = new SpeechAudioResponse
                {
                    Base64 = Convert.ToBase64String(audioBytes),
                    MimeType = mime,
                    Format = responseFormat
                },
                Warnings = warnings,
                ProviderMetadata = new Dictionary<string, JsonElement>
                {
                    [GetIdentifier()] = root.Clone()
                },
                Response = new ResponseData
                {
                    Timestamp = now,
                    ModelId = request.Model,
                    Body = root.Clone()
                }
            };
        }

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(contentBytes),
                MimeType = contentType ?? OpenAI.OpenAIProvider.MapToAudioMimeType(outputFormat),
                Format = outputFormat
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    statusCode = (int)resp.StatusCode,
                    contentType,
                    contentLength = contentBytes.LongLength
                }
            }
        };
    }

    private static T? GetSpeechProviderMetadata<T>(SpeechRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return default;

        if (!request.ProviderOptions.TryGetValue(providerId, out var element))
            return default;

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private sealed class AI302SpeechProviderMetadata
    {
        [JsonPropertyName("voice")]
        public string? Voice { get; set; }

        [JsonPropertyName("response_format")]
        public string? ResponseFormat { get; set; }

        [JsonPropertyName("stream_format")]
        public string? StreamFormat { get; set; }

        [JsonPropertyName("volume")]
        public double? Volume { get; set; }
    }
}
