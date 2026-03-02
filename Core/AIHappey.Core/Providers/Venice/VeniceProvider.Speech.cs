using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.Venice;

public partial class VeniceProvider
{
    private async Task<SpeechResponse> VeniceSpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var payload = CreateSpeechPayloadFromMetadata(metadata);

        // Unified mapping, while keeping provider metadata as raw passthrough.
        SetIfMissing(payload, "input", request.Text);

        if (!string.IsNullOrWhiteSpace(request.Model))
            SetIfMissing(payload, "model", request.Model.Trim());

        if (!string.IsNullOrWhiteSpace(request.Voice))
            SetIfMissing(payload, "voice", request.Voice.Trim());

        if (request.Speed is not null)
            SetIfMissing(payload, "speed", request.Speed.Value);

        var outputFormat = NormalizeSpeechFormat(request.OutputFormat);
        if (!string.IsNullOrWhiteSpace(outputFormat))
            SetIfMissing(payload, "response_format", outputFormat);

        // Strict mode default.
        SetIfMissing(payload, "response_format", "mp3");

        if (!payload.ContainsKey("model"))
            warnings.Add(new { type = "preferred", feature = "model", details = "Explicit model is preferred for Venice speech requests." });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(payload.ToJsonString(JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var rawError = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Venice speech request failed ({(int)response.StatusCode}): {rawError}");
        }

        var resolvedFormat = ResolveSpeechFormat(payload, response.Content.Headers.ContentType?.MediaType);
        var mimeType = ResolveSpeechMimeType(resolvedFormat, response.Content.Headers.ContentType?.MediaType);

        var providerMetadata = new JsonObject
        {
            ["endpoint"] = "v1/audio/speech",
            ["status"] = (int)response.StatusCode,
            ["contentType"] = response.Content.Headers.ContentType?.MediaType,
            ["request"] = JsonNode.Parse(payload.ToJsonString(JsonSerializerOptions.Web))
        };

        if (metadata.ValueKind == JsonValueKind.Object)
            providerMetadata["passthrough"] = JsonNode.Parse(metadata.GetRawText());

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = resolvedFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMetadata, JsonSerializerOptions.Web)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = payload.TryGetPropertyValue("model", out var modelNode) && modelNode is JsonValue modelValue && modelValue.TryGetValue<string>(out var model)
                    ? model
                    : request.Model,
                Body = new
                {
                    endpoint = "v1/audio/speech",
                    contentType = response.Content.Headers.ContentType?.MediaType
                }
            }
        };
    }

    private static JsonObject CreateSpeechPayloadFromMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return new JsonObject();

        return JsonNode.Parse(metadata.GetRawText()) as JsonObject ?? new JsonObject();
    }

    private static void SetIfMissing(JsonObject payload, string key, float value)
    {
        if (!payload.ContainsKey(key))
            payload[key] = value;
    }

    private static string? NormalizeSpeechFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        var normalized = format.Trim().ToLowerInvariant();
        return normalized switch
        {
            "mp3" or "opus" or "aac" or "flac" or "wav" or "pcm" => normalized,
            _ => normalized
        };
    }

    private static string ResolveSpeechFormat(JsonObject payload, string? contentType)
    {
        if (payload.TryGetPropertyValue("response_format", out var formatNode)
            && formatNode is JsonValue formatValue
            && formatValue.TryGetValue<string>(out var format)
            && !string.IsNullOrWhiteSpace(format))
        {
            return format.Trim().ToLowerInvariant();
        }

        return contentType?.ToLowerInvariant() switch
        {
            "audio/mpeg" => "mp3",
            "audio/opus" => "opus",
            "audio/aac" => "aac",
            "audio/flac" => "flac",
            "audio/wav" => "wav",
            "audio/pcm" => "pcm",
            _ => "mp3"
        };
    }

    private static string ResolveSpeechMimeType(string? format, string? contentType)
    {
        var normalized = format?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => contentType ?? "application/octet-stream"
        };
    }
}
