using AIHappey.Common.Extensions;
using System.Text.Json;
using System.Net.Mime;
using System.Text;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.DigitalOcean;

public partial class DigitalOceanProvider
{
    private async Task<ImageResponse> ImageRequestDigitalOcean(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var payload = BuildDigitalOceanImagePayload(request);
        RejectDigitalOceanImageStreaming(payload);

        using var response = await _client.PostAsync(
            "v1/images/generations",
            new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                MediaTypeNames.Application.Json),
            cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"DigitalOcean image request failed ({(int)response.StatusCode})."
                : $"DigitalOcean image request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = ExtractDigitalOceanImages(root);

        if (images.Count == 0)
            throw new InvalidOperationException("DigitalOcean image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = BuildDigitalOceanImageProviderMetadata(root),
            Usage = ExtractDigitalOceanImageUsage(root),
            Response = new()
            {
                Timestamp = ReadDigitalOceanCreated(root) ?? now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private Dictionary<string, object?> BuildDigitalOceanImagePayload(ImageRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["model"] = request.Model.Trim();
        payload["prompt"] = request.Prompt;

        if (request.N.HasValue)
            payload["n"] = request.N.Value;

        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size.Trim();

        return payload;
    }

    private static void RejectDigitalOceanImageStreaming(Dictionary<string, object?> payload)
    {
        if (payload.TryGetValue("stream", out var streamValue)
            && IsDigitalOceanImageStreamingEnabled(streamValue))
        {
            throw new NotSupportedException("DigitalOcean image streaming is not supported by this non-streaming ImageResponse path.");
        }
    }

    private static bool IsDigitalOceanImageStreamingEnabled(object? value)
        => value switch
        {
            true => true,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.String } element
                when bool.TryParse(element.GetString(), out var parsed) => parsed,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false
        };

    private static List<string> ExtractDigitalOceanImages(JsonElement root)
    {
        List<string> images = [];

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        var mediaType = DigitalOceanImageMediaTypeFromFormat(ReadDigitalOceanString(root, "output_format"));

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("b64_json", out var b64Json) && b64Json.ValueKind == JsonValueKind.String)
            {
                var value = b64Json.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    images.Add(value.ToDataUrl(mediaType));
            }

            if (item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                var value = url.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    images.Add(value);
            }
        }

        return [.. images.Distinct(StringComparer.Ordinal)];
    }

    private Dictionary<string, JsonElement>? BuildDigitalOceanImageProviderMetadata(JsonElement root)
    {
        var provider = new Dictionary<string, JsonElement>();
        var digitalOcean = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var propertyName in new[] { "background", "output_format", "quality", "size", "usage" })
        {
            if (root.TryGetProperty(propertyName, out var value)
                && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                digitalOcean[propertyName] = value.Clone();
            }
        }

        if (digitalOcean.Count > 0)
            provider[GetIdentifier()] = JsonSerializer.SerializeToElement(digitalOcean, JsonSerializerOptions.Web);

        return provider.Count == 0 ? null : provider;
    }

    private static ImageUsageData? ExtractDigitalOceanImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var usageData = new ImageUsageData
        {
            InputTokens = ReadDigitalOceanInt(usage, "input_tokens"),
            OutputTokens = ReadDigitalOceanInt(usage, "output_tokens"),
            TotalTokens = ReadDigitalOceanInt(usage, "total_tokens")
        };

        if (!usageData.InputTokens.HasValue
            && !usageData.OutputTokens.HasValue
            && !usageData.TotalTokens.HasValue)
        {
            return null;
        }

        return usageData;
    }

    private static DateTime? ReadDigitalOceanCreated(JsonElement root)
    {
        if (!root.TryGetProperty("created", out var created))
            return null;

        long? seconds = created.ValueKind switch
        {
            JsonValueKind.Number when created.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(created.GetString(), out var value) => value,
            _ => null
        };

        return seconds.HasValue ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime : null;
    }

    private static string? ReadDigitalOceanString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadDigitalOceanInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static string DigitalOceanImageMediaTypeFromFormat(string? outputFormat)
        => outputFormat?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };
}
