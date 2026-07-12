using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.IonRouter;

public partial class IonRouterProvider
{
    private static readonly JsonSerializerOptions IonRouterSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<SpeechResponse> IonRouterSpeechRequest(
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
        var payload = BuildIonRouterSpeechPayload(request, metadata);

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var responseFormat = ResolveIonRouterSpeechFormat(request, metadata, null);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, IonRouterSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(responseBytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"IonRouter speech request failed ({(int)response.StatusCode})."
                : $"IonRouter speech request failed ({(int)response.StatusCode}): {error}");
        }

        responseFormat = ResolveIonRouterSpeechFormat(request, metadata, contentType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(responseBytes),
                MimeType = ResolveIonRouterSpeechMimeType(responseFormat, contentType),
                Format = responseFormat
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private static Dictionary<string, object?> BuildIonRouterSpeechPayload(SpeechRequest request, JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        MergeIonRouterProviderMetadata(payload, metadata);

        payload["model"] = request.Model.Trim();
        payload["input"] = request.Text;

        if (!string.IsNullOrWhiteSpace(request.Voice))
            payload["voice"] = request.Voice.Trim();

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["response_format"] = request.OutputFormat.Trim().ToLowerInvariant();

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        return payload;
    }

    private static void MergeIonRouterProviderMetadata(Dictionary<string, object?> payload, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private static string ResolveIonRouterSpeechFormat(SpeechRequest request, JsonElement metadata, string? contentType)
    {
        var format = !string.IsNullOrWhiteSpace(request.OutputFormat)
            ? request.OutputFormat.Trim().ToLowerInvariant()
            : TryGetIonRouterString(metadata, "response_format", "responseFormat", "outputFormat", "format")?.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(format))
            return format;

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            if (contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return "mp3";
            if (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase) || contentType.Contains("wave", StringComparison.OrdinalIgnoreCase)) return "wav";
            if (contentType.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
            if (contentType.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "aac";
            if (contentType.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "flac";
            if (contentType.Contains("pcm", StringComparison.OrdinalIgnoreCase)) return "pcm";
        }

        return "wav";
    }

    private static string ResolveIonRouterSpeechMimeType(string? responseFormat, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        var format = (responseFormat ?? string.Empty).Trim().ToLowerInvariant();
        return format switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "application/octet-stream"
        };
    }

    private static string? TryGetIonRouterString(JsonElement element, params string[] names)
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
}
