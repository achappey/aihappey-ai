using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OrqRouter;

public partial class OrqRouterProvider
{
    private enum OrqRouterImageOperation
    {
        Generation,
        Edit,
        Variation
    }

    private async Task<ImageResponse> OrqRouterImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var files = request.Files?.Where(file => file is not null).ToList() ?? [];
        var operation = ResolveOrqRouterImageOperation(request, files.Count);

        if (operation is OrqRouterImageOperation.Generation && string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required for image generation.", nameof(request));

        if (operation is OrqRouterImageOperation.Edit && string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required for image edits.", nameof(request));

        if (operation is not OrqRouterImageOperation.Generation && files.Count == 0)
            throw new ArgumentException("At least one image file is required for image edits and variations.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        using var httpRequest = operation switch
        {
            OrqRouterImageOperation.Generation => BuildOrqRouterImageGenerationRequest(request),
            OrqRouterImageOperation.Edit => BuildOrqRouterImageEditRequest(request, files, warnings),
            OrqRouterImageOperation.Variation => BuildOrqRouterImageVariationRequest(request, files[0], warnings),
            _ => throw new NotSupportedException($"Unsupported OrqRouter image operation {operation}.")
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OrqRouter image request failed ({(int)response.StatusCode})."
                : $"OrqRouter image request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var images = ExtractOrqRouterImages(root);

        if (images.Count == 0)
            throw new InvalidOperationException("OrqRouter image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            Usage = ExtractOrqRouterImageUsage(root),
            Warnings = warnings,
            ProviderMetadata = BuildOrqRouterProviderMetadata(root),
            Response = new()
            {
                Timestamp = ResolveOrqRouterTimestamp(root, now),
                ModelId = ReadOrqRouterString(root, "model")?.ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static OrqRouterImageOperation ResolveOrqRouterImageOperation(ImageRequest request, int fileCount)
    {
        if (fileCount == 0)
            return OrqRouterImageOperation.Generation;

        if (fileCount == 1 && string.IsNullOrWhiteSpace(request.Prompt) && request.Mask is null)
            return OrqRouterImageOperation.Variation;

        return OrqRouterImageOperation.Edit;
    }

    private HttpRequestMessage BuildOrqRouterImageGenerationRequest(ImageRequest request)
    {
        var providerOptions = ReadOrqRouterProviderOptions(request.ProviderOptions);
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["model"] = request.Model
        };

        AddOrqRouterCommonImageJsonOptions(payload, request);
        MergeOrqRouterProviderOptions(payload, providerOptions, ReservedOrqRouterImageJsonKeys);

        return new HttpRequestMessage(HttpMethod.Post, "v2/router/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OrqRouterJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
    }

    private HttpRequestMessage BuildOrqRouterImageEditRequest(ImageRequest request, List<ImageFile> files, List<object> warnings)
    {
        var providerOptions = ReadOrqRouterProviderOptions(request.ProviderOptions);
        var inputFiles = files.Take(16).ToList();

        if (files.Count > inputFiles.Count)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = $"OrqRouter image edits support up to 16 input images. Used first {inputFiles.Count} images."
            });
        }

        var form = new MultipartFormDataContent();
        AddOrqRouterMultipartString(form, "model", request.Model);
        AddOrqRouterMultipartString(form, "prompt", request.Prompt);
        AddOrqRouterCommonImageMultipartOptions(form, request);

        for (var i = 0; i < inputFiles.Count; i++)
            form.Add(CreateOrqRouterImageFileContent(inputFiles[i]), "image", GetOrqRouterImageFilename(inputFiles[i], i));

        if (request.Mask is not null)
            form.Add(CreateOrqRouterImageFileContent(request.Mask), "mask", GetOrqRouterImageFilename(request.Mask, 0, "mask"));

        AddOrqRouterMultipartProviderOptions(form, providerOptions, ReservedOrqRouterImageMultipartKeys);

        return new HttpRequestMessage(HttpMethod.Post, "v2/router/images/edits")
        {
            Content = form
        };
    }

    private HttpRequestMessage BuildOrqRouterImageVariationRequest(ImageRequest request, ImageFile file, List<object> warnings)
    {
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            warnings.Add(new { type = "unsupported", feature = "prompt" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var providerOptions = ReadOrqRouterProviderOptions(request.ProviderOptions);
        var form = new MultipartFormDataContent();
        AddOrqRouterMultipartString(form, "model", request.Model);
        AddOrqRouterCommonImageMultipartOptions(form, request, includeQuality: false);
        form.Add(CreateOrqRouterImageFileContent(file), "image", GetOrqRouterImageFilename(file, 0));
        AddOrqRouterMultipartProviderOptions(form, providerOptions, ReservedOrqRouterImageMultipartKeys);

        return new HttpRequestMessage(HttpMethod.Post, "v2/router/images/variations")
        {
            Content = form
        };
    }

    private static readonly HashSet<string> ReservedOrqRouterImageJsonKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompt", "model", "n", "size", "seed", "background", "moderation", "output_compression",
        "output_format", "quality", "response_format", "style"
    };

    private static readonly HashSet<string> ReservedOrqRouterImageMultipartKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompt", "model", "n", "size", "quality", "response_format", "user", "image", "mask"
    };

    private static void AddOrqRouterCommonImageJsonOptions(Dictionary<string, object?> payload, ImageRequest request)
    {
        if (request.N is not null)
            payload["n"] = request.N.Value;

        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size;

        if (request.Seed is not null)
            payload["seed"] = request.Seed.Value;

        if (request.AspectRatio is not null && string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = ResolveOrqRouterImageSizeFromAspectRatio(request.AspectRatio);
    }

    private static void AddOrqRouterCommonImageMultipartOptions(
        MultipartFormDataContent form,
        ImageRequest request,
        bool includeQuality = true)
    {
        if (request.N is not null)
            AddOrqRouterMultipartString(form, "n", request.N.Value.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(request.Size))
            AddOrqRouterMultipartString(form, "size", request.Size);
        else if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            AddOrqRouterMultipartString(form, "size", ResolveOrqRouterImageSizeFromAspectRatio(request.AspectRatio));

        if (includeQuality)
        {
            // Quality and response_format are provider-specific for Orq; callers can pass exact values through providerOptions.orq.
        }
    }

    private static string? ResolveOrqRouterImageSizeFromAspectRatio(string? aspectRatio)
    {
        if (string.IsNullOrWhiteSpace(aspectRatio))
            return null;

        return aspectRatio.Trim().ToLowerInvariant() switch
        {
            "1:1" => "1024x1024",
            "2:3" or "3:4" or "9:16" => "1024x1536",
            "3:2" or "4:3" or "16:9" => "1536x1024",
            _ => null
        };
    }

    private static HttpContent CreateOrqRouterImageFileContent(ImageFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OrqRouter image edits and variations require uploaded image files. URL inputs are not supported for multipart image fields.");

        var rawData = file.Data.RemoveDataUrlPrefix();
        var bytes = Convert.FromBase64String(rawData);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType);
        return content;
    }

    private static string GetOrqRouterImageFilename(ImageFile file, int index, string prefix = "image")
    {
        var extension = NormalizeOrqRouterMediaType(file.MediaType, MediaTypeNames.Image.Png).ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Jpeg => "jpg",
            "image/webp" => "webp",
            "image/gif" => "gif",
            _ => "png"
        };

        return $"{prefix}-{index}.{extension}";
    }

    private static List<string> ExtractOrqRouterImages(JsonElement root)
    {
        List<string> images = [];

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        var rootOutputFormat = ReadOrqRouterString(root, "output_format");

        foreach (var image in data.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object)
                continue;

            var mediaType = ResolveOrqRouterImageMediaType(
                ReadOrqRouterString(image, "output_format", "format") ?? rootOutputFormat,
                ReadOrqRouterString(image, "media_type", "mime_type"));

            foreach (var propertyName in new[] { "b64_json", "url", "image", "data" })
            {
                if (!image.TryGetProperty(propertyName, out var valueEl) || valueEl.ValueKind != JsonValueKind.String)
                    continue;

                var value = valueEl.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                images.Add(NormalizeOrqRouterImageOutput(value, mediaType));
                break;
            }
        }

        return [.. images.Distinct(StringComparer.Ordinal)];
    }

    private static ImageUsageData? ExtractOrqRouterImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var usageData = new ImageUsageData
        {
            InputTokens = ReadOrqRouterInt(usage, "input_tokens"),
            OutputTokens = ReadOrqRouterInt(usage, "output_tokens"),
            TotalTokens = ReadOrqRouterInt(usage, "total_tokens")
        };

        return usageData.InputTokens.HasValue || usageData.OutputTokens.HasValue || usageData.TotalTokens.HasValue
            ? usageData
            : null;
    }

    private static string NormalizeOrqRouterImageOutput(string value, string mediaType)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return value.ToDataUrl(mediaType);
    }

    private static string ResolveOrqRouterImageMediaType(string? outputFormat, string? mediaType)
    {
        if (!string.IsNullOrWhiteSpace(mediaType))
            return mediaType;

        return (outputFormat ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            "png" => MediaTypeNames.Image.Png,
            _ => MediaTypeNames.Image.Png
        };
    }
}
