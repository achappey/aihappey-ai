using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.OhMyGPT;

public partial class OhMyGPTProvider
{
    private static readonly JsonSerializerOptions OhMyGptImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string DefaultOhMyGptImageOutputFormat = "png";

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required for image generation and image edits.", nameof(request));

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var files = request.Files?.Where(file => file is not null).ToList() ?? [];

        if (request.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        using var httpRequest = files.Count == 0
            ? BuildOhMyGptImageGenerationRequest(request, warnings)
            : BuildOhMyGptImageEditRequest(request, files, warnings);

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OhMyGPT image request failed ({(int)resp.StatusCode})."
                : $"OhMyGPT image request failed ({(int)resp.StatusCode}): {raw}");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        var images = ExtractOhMyGptImages(root);
        if (images.Count == 0)
            throw new InvalidOperationException("OhMyGPT image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractOhMyGptImageUsage(root),
            ProviderMetadata = BuildOhMyGptImageProviderMetadata(root, GetIdentifier()),
            Response = new()
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = ReadOhMyGptImageString(root, "model")?.ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private HttpRequestMessage BuildOhMyGptImageGenerationRequest(ImageRequest request, List<object> warnings)
    {
        var metadata = request.GetProviderMetadata<OpenAiImageProviderMetadata>(GetIdentifier());
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        AddOhMyGptImageCommonOptions(payload, request, metadata?.Quality, warnings);

        if (!string.IsNullOrWhiteSpace(metadata?.Background))
            payload["background"] = metadata.Background;

        if (!string.IsNullOrWhiteSpace(metadata?.Moderation))
            payload["moderation"] = metadata.Moderation;

        payload["output_format"] = DefaultOhMyGptImageOutputFormat;

        return new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OhMyGptImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
    }

    private HttpRequestMessage BuildOhMyGptImageEditRequest(ImageRequest request, List<ImageFile> files, List<object> warnings)
    {
        var metadata = request.GetProviderMetadata<OpenAiImageEditProviderMetadata>(GetIdentifier());
        var inputFiles = files.Take(16).ToList();

        if (files.Count > inputFiles.Count)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = $"OhMyGPT image edits support up to 16 input images. Used first {inputFiles.Count} images."
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["images"] = inputFiles.Select(ToOhMyGptImageReference).ToList()
        };

        AddOhMyGptImageCommonOptions(payload, request, metadata?.Quality, warnings);

        if (!string.IsNullOrWhiteSpace(metadata?.Background))
            payload["background"] = metadata.Background;

        if (!string.IsNullOrWhiteSpace(metadata?.InputFidelity))
            payload["input_fidelity"] = metadata.InputFidelity;

        payload["output_format"] = DefaultOhMyGptImageOutputFormat;

        if (request.Mask is not null)
            payload["mask"] = ToOhMyGptImageReference(request.Mask);

        return new HttpRequestMessage(HttpMethod.Post, "v1/images/edits")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OhMyGptImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
    }

    private static void AddOhMyGptImageCommonOptions(
        Dictionary<string, object?> payload,
        ImageRequest request,
        string? quality,
        List<object> warnings)
    {
        if (request.N.HasValue)
            payload["n"] = request.N.Value;

        var size = ResolveOhMyGptImageSize(request, warnings);
        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        if (!string.IsNullOrWhiteSpace(quality))
            payload["quality"] = quality;
    }

    private static string? ResolveOhMyGptImageSize(ImageRequest request, List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Size))
            return request.Size;

        if (string.IsNullOrWhiteSpace(request.AspectRatio))
            return null;

        var size = request.AspectRatio.Trim().ToLowerInvariant() switch
        {
            "1:1" => "1024x1024",
            "2:3" or "3:4" or "9:16" => "1024x1536",
            "3:2" or "4:3" or "16:9" => "1536x1024",
            _ => null
        };

        if (size is null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspectRatio",
                details = $"Requested aspect ratio {request.AspectRatio} not supported. Used default settings."
            });
        }

        return size;
    }

    private static Dictionary<string, object?> ToOhMyGptImageReference(ImageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (string.Equals(file.Type, "file_id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.Type, "fileId", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["file_id"] = file.Data
            };
        }

        return new Dictionary<string, object?>
        {
            ["image_url"] = NormalizeOhMyGptImageInput(file)
        };
    }

    private static string NormalizeOhMyGptImageInput(ImageFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType;

        return file.Data.ToDataUrl(mediaType);
    }

    private static List<string> ExtractOhMyGptImages(JsonElement root)
    {
        List<string> images = [];

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        var outputFormat = ReadOhMyGptImageString(root, "output_format");
        var mediaType = OhMyGptImageMediaTypeFromFormat(outputFormat);

        foreach (var image in data.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object)
                continue;

            if (image.TryGetProperty("b64_json", out var b64Json) && b64Json.ValueKind == JsonValueKind.String)
            {
                var value = b64Json.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    images.Add(NormalizeOhMyGptImageOutput(value, mediaType));
            }

            if (image.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                var value = url.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    images.Add(value);
            }
        }

        return [.. images.Distinct(StringComparer.Ordinal)];
    }

    private static ImageUsageData? ExtractOhMyGptImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var usageData = new ImageUsageData
        {
            InputTokens = ReadOhMyGptImageInt(usage, "input_tokens"),
            OutputTokens = ReadOhMyGptImageInt(usage, "output_tokens"),
            TotalTokens = ReadOhMyGptImageInt(usage, "total_tokens"),
        };

        if (!usageData.InputTokens.HasValue
            && !usageData.OutputTokens.HasValue
            && !usageData.TotalTokens.HasValue)
        {
            return null;
        }

        return usageData;
    }

    private static Dictionary<string, JsonElement>? BuildOhMyGptImageProviderMetadata(JsonElement root, string providerKey)
    {
        var providerMetadata = new Dictionary<string, JsonElement>();

        foreach (var propertyName in OhMyGptImageProviderMetadataProperties)
        {
            if (root.TryGetProperty(propertyName, out var property)
                && property.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                providerMetadata[propertyName] = property.Clone();
            }
        }

        return providerKey.CreatePrimitiveProviderMetadata(providerMetadata);
    }

    private static readonly string[] OhMyGptImageProviderMetadataProperties =
    [
        "usage",
        "created",
        "background",
        "output_format",
        "quality",
        "size",
        "model"
    ];

    private static string NormalizeOhMyGptImageOutput(string value, string mediaType)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return value.ToDataUrl(mediaType);
    }

    private static string OhMyGptImageMediaTypeFromFormat(string? outputFormat)
        => outputFormat?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };

    private static string? ReadOhMyGptImageString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? ReadOhMyGptImageInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }
}
