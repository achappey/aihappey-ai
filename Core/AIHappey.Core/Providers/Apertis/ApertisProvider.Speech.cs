using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Apertis;

public partial class ApertisProvider
{
    private static readonly JsonSerializerOptions ApertisSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildApertisSpeechPayload(request, metadata);

        if (!payload.TryGetValue("voice", out var voiceValue) || string.IsNullOrWhiteSpace(ToApertisText(voiceValue)))
            throw new ArgumentException("Voice is required for Apertis speech endpoint.", nameof(request));

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var json = JsonSerializer.Serialize(payload, ApertisSpeechJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(responseBytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Apertis speech request failed ({(int)response.StatusCode})."
                : $"Apertis speech request failed ({(int)response.StatusCode}): {error}");
        }

        var requestedFormat = ResolveApertisSpeechFormat(request, metadata, contentType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(responseBytes),
                MimeType = contentType ?? OpenAI.OpenAIProvider.MapToAudioMimeType(requestedFormat),
                Format = requestedFormat
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = new
                {
                    statusCode = (int)response.StatusCode,
                    contentType,
                    contentLength = responseBytes.LongLength
                }
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private static Dictionary<string, object?> BuildApertisSpeechPayload(SpeechRequest request, JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["model"] = request.Model.Trim();
        payload["input"] = request.Text;

        if (!string.IsNullOrWhiteSpace(request.Voice))
            payload["voice"] = request.Voice.Trim();

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["response_format"] = request.OutputFormat.Trim().ToLowerInvariant();

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["instructions"] = request.Instructions;

        return payload;
    }

    private static string ResolveApertisSpeechFormat(SpeechRequest request, JsonElement metadata, string? contentType)
    {
        var requestedFormat = !string.IsNullOrWhiteSpace(request.OutputFormat)
            ? request.OutputFormat.Trim().ToLowerInvariant()
            : TryGetApertisString(metadata, "response_format", "responseFormat", "outputFormat", "format")?.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(requestedFormat))
            return requestedFormat;

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            if (contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return "mp3";
            if (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase) || contentType.Contains("wave", StringComparison.OrdinalIgnoreCase)) return "wav";
            if (contentType.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
            if (contentType.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "aac";
            if (contentType.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "flac";
            if (contentType.Contains("pcm", StringComparison.OrdinalIgnoreCase)) return "pcm";
        }

        return "mp3";
    }

    private static string? TryGetApertisString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.GetRawText();
        }

        return null;
    }

    private static string? ToApertisText(object? value)
        => value switch
        {
            null => null,
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement element => element.GetRawText(),
            _ => value.ToString()
        };
}
