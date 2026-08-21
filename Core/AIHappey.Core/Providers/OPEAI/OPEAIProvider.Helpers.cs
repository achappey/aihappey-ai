using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.OPEAI;

public partial class OPEAIProvider
{
    private static readonly JsonSerializerOptions OPEAIJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static JsonObject CreateOPEAIPayload(JsonElement? rawOptions)
    {
        var payload = new JsonObject();
        if (rawOptions is not { ValueKind: JsonValueKind.Object })
            return payload;

        foreach (var property in rawOptions.Value.EnumerateObject())
            payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());

        return payload;
    }

    private static JsonObject CreateOPEAIPayload(Dictionary<string, JsonElement>? additionalProperties)
    {
        var payload = new JsonObject();
        foreach (var property in additionalProperties ?? [])
            payload[property.Key] = JsonNode.Parse(property.Value.GetRawText());

        return payload;
    }

    private static JsonElement? GetOPEAIProviderOptions(Dictionary<string, JsonElement>? providerOptions)
        => providerOptions?.TryGetValue(nameof(OPEAI).ToLowerInvariant(), out var options) == true
            ? options
            : null;

    private HttpRequestMessage CreateOPEAIJsonRequest(string endpoint, JsonObject payload)
        => new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(OPEAIJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private async Task<(JsonElement Root, Dictionary<string, string> Headers)> SendOPEAIJsonAsync(
        string endpoint,
        JsonObject payload,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = CreateOPEAIJsonRequest(endpoint, payload);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OPE AI {operation} failed ({(int)response.StatusCode}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            return (document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"OPE AI {operation} returned invalid JSON: {raw}", ex);
        }
    }

    private static int? ReadOPEAIInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static string ResolveOPEAIAudioMimeType(string? format, string? responseContentType = null)
    {
        if (!string.IsNullOrWhiteSpace(responseContentType)
            && !string.Equals(responseContentType, MediaTypeNames.Application.Octet, StringComparison.OrdinalIgnoreCase))
            return responseContentType;

        return format?.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "ogg" => "audio/ogg",
            "wav" or "wave" => "audio/wav",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "pcm" => "audio/pcm",
            _ => responseContentType ?? "audio/wav"
        };
    }

    private static string ResolveOPEAIAudioFormat(string? format, string mimeType)
        => !string.IsNullOrWhiteSpace(format)
            ? format.Trim().ToLowerInvariant()
            : mimeType.ToLowerInvariant() switch
            {
                "audio/mpeg" => "mp3",
                "audio/ogg" => "ogg",
                "audio/flac" => "flac",
                "audio/aac" => "aac",
                "audio/pcm" => "pcm",
                _ => "wav"
            };
}
