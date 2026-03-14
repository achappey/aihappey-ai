using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AKI;

public partial class AKIProvider
{
    private static readonly JsonSerializerOptions AkiImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex AkiEncodedBinaryRegex = new(
        "^(?<format>[a-zA-Z0-9.+/-]+);base64,(?<data>[A-Za-z0-9+/=\\r\\n]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var apiKey = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"No {nameof(AKI)} API key.");

        if (request.N is > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "AKI image generation currently returns the provider result set for a single request. Requested n>1 is ignored."
            });
        }

        var files = request.Files?.ToList() ?? [];

        if (files.Count > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "AKI image generation currently maps only the first input file to 'image'."
            });
        }

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask",
                details = "AKI image generation does not currently map 'mask' to a provider parameter."
            });
        }

        var endpoint = request.Model;
        var payload = BuildImagePayload(request, apiKey, files.FirstOrDefault(), warnings);

        var json = JsonSerializer.Serialize(payload, AkiImageJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"api/call/{Uri.EscapeDataString(endpoint)}")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"AKI image generation failed ({(int)response.StatusCode})."
                : $"AKI image generation failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        if (root.TryGetProperty("success", out var successEl)
            && successEl.ValueKind is JsonValueKind.False)
        {
            var errorCode = TryGetString(root, "error_code");
            var error = TryGetString(root, "error") ?? "AKI image generation failed.";
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorCode)
                ? error
                : $"{errorCode}: {error}");
        }

        var images = ExtractImages(root);
        if (images.Count == 0)
            throw new InvalidOperationException("AKI image generation returned no images.");

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
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private Dictionary<string, object?> BuildImagePayload(
        ImageRequest request,
        string apiKey,
        ImageFile? inputFile,
        List<object> warnings)
    {
        var (width, height) = ResolveImageSize(request);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["key"] = apiKey,
            ["prompt"] = request.Prompt,
            ["width"] = width,
            ["height"] = height,
            ["seed"] = request.Seed ?? -1
        };

        if (inputFile is not null)
            payload["image"] = NormalizeImageInput(inputFile);

        AddOptionalString(payload, metadata, "negative_prompt");
        AddOptionalInt32(payload, metadata, "steps");
        AddOptionalDouble(payload, metadata, "true_cfg_scale");
        AddOptionalString(payload, metadata, "quality");

        MergeProviderOptions(payload, metadata, warnings);

        return payload;
    }

    private static (int width, int height) ResolveImageSize(ImageRequest request)
    {
        var parsed = ParseImageSize(request.Size);
        if (parsed.width.HasValue && parsed.height.HasValue)
            return (parsed.width.Value, parsed.height.Value);

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

    private static (int? width, int? height) ParseImageSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size))
            return (null, null);

        var normalized = size.Trim().Replace(":", "x", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("*", "x", StringComparison.OrdinalIgnoreCase);

        var parts = normalized.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return (null, null);

        if (!int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
            return (null, null);

        return (width, height);
    }

    private static string NormalizeImageInput(ImageFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            return file.Data;

        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return file.Data;

        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;

        return $"{NormalizeImageFormat(file.MediaType)};base64,{file.Data}";
    }

    private static List<string> ExtractImages(JsonElement root)
    {
        var results = new List<string>();

        if (!root.TryGetProperty("images", out var imagesEl) || imagesEl.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in imagesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            results.Add(DecodeAkiImage(value));
        }

        return results;
    }

    private static string DecodeAkiImage(string encoded)
    {
        var trimmed = encoded.Trim();

        if (trimmed.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        var match = AkiEncodedBinaryRegex.Match(trimmed);
        if (match.Success)
        {
            var format = match.Groups["format"].Value;
            var base64 = match.Groups["data"].Value.Replace("\r", string.Empty).Replace("\n", string.Empty);
            return base64.ToDataUrl(NormalizeImageFormat(format));
        }

        var separatorIndex = trimmed.IndexOf(',');
        if (separatorIndex > 0)
        {
            var format = trimmed[..separatorIndex].Trim();
            var base64 = trimmed[(separatorIndex + 1)..].Trim();

            if (LooksLikeBase64(base64))
                return base64.ToDataUrl(NormalizeImageFormat(format));
        }

        if (LooksLikeBase64(trimmed))
            return trimmed.ToDataUrl(MediaTypeNames.Image.Png);

        throw new InvalidOperationException("AKI image payload format is not supported.");
    }

    private static bool LooksLikeBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        Span<byte> buffer = new byte[(value.Length * 3) / 4 + 4];
        return Convert.TryFromBase64String(value, buffer, out _);
    }

    private static string NormalizeImageFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return MediaTypeNames.Image.Png;

        var normalized = format.Trim();
        if (normalized.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[5..];

        if (normalized.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return normalized;

        return normalized.ToLowerInvariant() switch
        {
            "jpg" => MediaTypeNames.Image.Jpeg,
            "jpeg" => MediaTypeNames.Image.Jpeg,
            "png" => MediaTypeNames.Image.Png,
            "webp" => "image/webp",
            "gif" => MediaTypeNames.Image.Gif,
            "bmp" => "image/bmp",
            "svg" or "svg+xml" => "image/svg+xml",
            _ => $"image/{normalized}"
        };
    }

    private static void AddOptionalString(Dictionary<string, object?> payload, JsonElement metadata, string propertyName)
    {
        var value = TryGetString(metadata, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
            payload[propertyName] = value;
    }

    private static void AddOptionalInt32(Dictionary<string, object?> payload, JsonElement metadata, string propertyName)
    {
        var value = TryGetInt32(metadata, propertyName);
        if (value.HasValue)
            payload[propertyName] = value.Value;
    }

    private static void AddOptionalDouble(Dictionary<string, object?> payload, JsonElement metadata, string propertyName)
    {
        var value = TryGetDouble(metadata, propertyName);
        if (value.HasValue)
            payload[propertyName] = value.Value;
    }

    private static void MergeProviderOptions(Dictionary<string, object?> payload, JsonElement metadata, List<object> warnings)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            if (payload.ContainsKey(property.Name))
                continue;

            payload[property.Name] = property.Value;
            warnings.Add(new
            {
                type = "passthrough",
                feature = property.Name,
                details = $"Forwarded provider option '{property.Name}' to AKI request payload."
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

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
            return number;

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number))
            return number;

        return null;
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
            return number;

        if (property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), out number))
            return number;

        return null;
    }
}
