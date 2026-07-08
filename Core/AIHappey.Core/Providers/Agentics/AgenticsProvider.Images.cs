using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Agentics;

public partial class AgenticsProvider
{
    private static readonly JsonSerializerOptions AgenticsImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> AgenticsImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var payload = BuildAgenticsImagePayload(request, warnings);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AgenticsImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Agentics image generation failed ({(int)response.StatusCode})."
                : $"Agentics image generation failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var images = await ExtractAgenticsImagesAsync(root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("Agentics image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new()
            {
                Timestamp = ResolveAgenticsImageTimestamp(root, now),
                ModelId = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                    ? modelEl.GetString()?.ToModelId(GetIdentifier()) ?? request.Model.ToModelId(GetIdentifier())
                    : request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public static Dictionary<string, object?> BuildAgenticsImagePayload(ImageRequest request, List<object>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        warnings ??= [];

        var metadata = request.GetProviderMetadata<JsonElement>(nameof(Agentics).ToLowerInvariant());
        var (width, height) = ResolveAgenticsImageSize(request);

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["model"] = request.Model,
            ["n"] = request.N,
            ["width"] = width,
            ["height"] = height,
            ["ratio"] = request.AspectRatio
        };

        var format = TryGetString(metadata, "format") ?? TryGetString(metadata, "response_format") ?? "b64_json";
        if (!string.IsNullOrWhiteSpace(format))
            payload["format"] = format;

        AddOptionalString(payload, metadata, "negative_prompt");
        AddOptionalString(payload, metadata, "style");

        if (request.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Agentics image generation currently supports text-to-image only; input files are ignored."
            });
        }

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask",
                details = "Agentics image generation currently supports text-to-image only; masks are ignored."
            });
        }

        if (request.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed",
                details = "Agentics image generation does not document a seed parameter; seed is ignored."
            });
        }

        MergeAgenticsProviderOptions(payload, metadata, warnings);

        return payload;
    }

    private static (int width, int height) ResolveAgenticsImageSize(ImageRequest request)
    {
        var normalizedSize = request.Size?.Replace(":", "x", StringComparison.OrdinalIgnoreCase)
            .Replace("*", "x", StringComparison.OrdinalIgnoreCase);

        var width = string.IsNullOrWhiteSpace(normalizedSize)
            ? null
            : new ImageRequest { Size = normalizedSize }.GetImageWidth();
        var height = string.IsNullOrWhiteSpace(normalizedSize)
            ? null
            : new ImageRequest { Size = normalizedSize }.GetImageHeight();

        if (width.HasValue && height.HasValue)
            return (width.Value, height.Value);

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            var inferred = request.AspectRatio.InferSizeFromAspectRatio(
                minWidth: 128,
                maxWidth: 2048,
                minHeight: 128,
                maxHeight: 2048);

            if (inferred is not null)
                return inferred.Value;
        }

        return (1024, 1024);
    }

    private async Task<List<string>> ExtractAgenticsImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in dataEl.EnumerateArray())
        {
            if (TryExtractAgenticsImageString(item, out var imageValue))
                images.Add(await NormalizeAgenticsImageAsync(imageValue, cancellationToken));
        }

        return images;
    }

    private static bool TryExtractAgenticsImageString(JsonElement item, out string imageValue)
    {
        imageValue = string.Empty;

        if (item.ValueKind == JsonValueKind.String)
        {
            imageValue = item.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(imageValue);
        }

        if (item.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var propertyName in new[] { "b64_json", "url", "image", "data" })
        {
            if (!item.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
                continue;

            imageValue = property.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(imageValue);
        }

        return false;
    }

    private async Task<string> NormalizeAgenticsImageAsync(string imageValue, CancellationToken cancellationToken)
    {
        var trimmed = imageValue.Trim();

        if (trimmed.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var response = await _client.GetAsync(uri, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!response.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"Failed to download Agentics image from returned URL ({(int)response.StatusCode}).");

            var mediaType = response.Content.Headers.ContentType?.MediaType
                ?? GuessAgenticsImageMediaType(trimmed)
                ?? MediaTypeNames.Image.Png;

            return Convert.ToBase64String(bytes).ToDataUrl(mediaType);
        }

        return trimmed.RemoveDataUrlPrefix().ToDataUrl(MediaTypeNames.Image.Png);
    }

    private static DateTime ResolveAgenticsImageTimestamp(JsonElement root, DateTime fallback)
    {
        if (root.TryGetProperty("created", out var createdEl)
            && createdEl.ValueKind == JsonValueKind.Number
            && createdEl.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }

        return fallback;
    }

    private static void AddOptionalString(Dictionary<string, object?> payload, JsonElement metadata, string propertyName)
    {
        var value = TryGetString(metadata, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
            payload[propertyName] = value;
    }

    private static void MergeAgenticsProviderOptions(Dictionary<string, object?> payload, JsonElement metadata, List<object> warnings)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            if (payload.ContainsKey(property.Name) || property.Name is "response_format")
                continue;

            payload[property.Name] = property.Value;
            warnings.Add(new
            {
                type = "passthrough",
                feature = property.Name,
                details = $"Forwarded provider option '{property.Name}' to Agentics image request payload."
            });
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static string? GuessAgenticsImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.Contains(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";
        if (url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) || url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Jpeg;
        if (url.Contains(".png", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Png;

        return null;
    }
}
