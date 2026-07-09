using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    private static readonly JsonSerializerOptions OpenAiImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string DefaultOpenAiImageOutputFormat = "png";

    private static readonly IReadOnlyDictionary<string, OpenAiImageTokenPricing> OpenAiImagePricing =
        new Dictionary<string, OpenAiImageTokenPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-image-2"] = new(8.00m, 2.00m, 30.00m, 5.00m, 1.25m, null),
            ["gpt-image-1.5"] = new(8.00m, 2.00m, 32.00m, 5.00m, 1.25m, 10.00m),
            ["gpt-image-1-mini"] = new(2.50m, 0.25m, 8.00m, 2.00m, 0.20m, null),
            ["gpt-image-1"] = new(10.00m, 2.50m, 40.00m, 5.00m, 1.25m, null),
            ["chatgpt-image-latest"] = new(8.00m, 2.00m, 32.00m, 5.00m, 1.25m, 10.00m)
        };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageRequest);

        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var files = imageRequest.Files?.Where(file => file is not null).ToList() ?? [];

        if (files.Count != 1 && string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required for image generation and image edits.", nameof(imageRequest));

        if (imageRequest.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        var operation = ResolveOpenAiImageOperation(files.Count);
        using var httpRequest = operation switch
        {
            OpenAiImageOperation.Generation => BuildOpenAiImageGenerationRequest(imageRequest, warnings),
            OpenAiImageOperation.Variation => BuildOpenAiImageVariationRequest(imageRequest, files[0], warnings),
            OpenAiImageOperation.Edit => BuildOpenAiImageEditRequest(imageRequest, files, warnings),
            _ => throw new NotSupportedException($"Unsupported OpenAI image operation {operation}.")
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OpenAI image request failed ({(int)resp.StatusCode})."
                : $"OpenAI image request failed ({(int)resp.StatusCode}): {raw}");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        var results = ExtractOpenAiImages(root);
        if (results.Count == 0)
            throw new InvalidOperationException("OpenAI image response did not contain generated images.");

        var cost = CalculateOpenAiImageCost(imageRequest.Model, root);

        return new ImageResponse()
        {
            Images = results,
            Warnings = warnings,
            Usage = ExtractOpenAiImageUsage(root),
            ProviderMetadata = BuildOpenAiImageProviderMetadata(root, GetIdentifier(), cost),
            Response = new()
            {
                Timestamp = now,
                ModelId = ReadOpenAiImageString(root, "model")?.ToModelId(GetIdentifier())
                    ?? imageRequest.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static OpenAiImageOperation ResolveOpenAiImageOperation(int fileCount)
        => fileCount switch
        {
            0 => OpenAiImageOperation.Generation,
            1 => OpenAiImageOperation.Variation,
            _ => OpenAiImageOperation.Edit
        };

    private HttpRequestMessage BuildOpenAiImageGenerationRequest(ImageRequest request, List<object> warnings)
    {
        var metadata = request.GetProviderMetadata<OpenAiImageProviderMetadata>(GetIdentifier());
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        AddOpenAiImageCommonOptions(payload, request, metadata?.Quality, warnings, OpenAiImageOperation.Generation);

        if (!string.IsNullOrWhiteSpace(metadata?.Background) && IsGptOpenAiImageModel(request.Model))
            payload["background"] = metadata.Background;

        if (!string.IsNullOrWhiteSpace(metadata?.Moderation) && IsGptOpenAiImageModel(request.Model))
            payload["moderation"] = metadata.Moderation;

        if (IsGptOpenAiImageModel(request.Model))
            payload["output_format"] = DefaultOpenAiImageOutputFormat;
        else
            payload["response_format"] = "b64_json";

        return new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OpenAiImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
    }

    private HttpRequestMessage BuildOpenAiImageEditRequest(ImageRequest request, List<ImageFile> files, List<object> warnings)
    {
        var metadata = request.GetProviderMetadata<OpenAiImageEditProviderMetadata>(GetIdentifier());
        var inputFiles = files.Take(16).ToList();

        if (files.Count > inputFiles.Count)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = $"OpenAI image edits support up to 16 input images. Used first {inputFiles.Count} images."
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["images"] = inputFiles.Select(ToOpenAiImageReference).ToList()
        };

        AddOpenAiImageCommonOptions(payload, request, metadata?.Quality, warnings, OpenAiImageOperation.Edit);

        if (!string.IsNullOrWhiteSpace(metadata?.Background))
            payload["background"] = metadata.Background;

        if (!string.IsNullOrWhiteSpace(metadata?.InputFidelity))
            payload["input_fidelity"] = metadata.InputFidelity;

        if (IsGptOpenAiImageModel(request.Model))
            payload["output_format"] = DefaultOpenAiImageOutputFormat;

        if (request.Mask is not null)
            payload["mask"] = ToOpenAiImageReference(request.Mask);

        return new HttpRequestMessage(HttpMethod.Post, "v1/images/edits")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OpenAiImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
    }

    private HttpRequestMessage BuildOpenAiImageVariationRequest(ImageRequest request, ImageFile file, List<object> warnings)
    {
        if (!string.Equals(NormalizeOpenAiImageModel(request.Model), "dall-e-2", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "model",
                details = "OpenAI image variations are only supported by dall-e-2. The request model was forwarded unchanged."
            });
        }

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "prompt",
                details = "OpenAI image variations do not accept a prompt. Prompt was omitted."
            });
        }

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspectRatio",
                details = "OpenAI image variations only accept square size values. Aspect ratio was omitted."
            });
        }

        var content = new MultipartFormDataContent();
        AddOpenAiMultipartString(content, "model", request.Model);
        AddOpenAiMultipartString(content, "response_format", "b64_json");

        if (request.N.HasValue)
            AddOpenAiMultipartString(content, "n", request.N.Value.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(request.Size))
            AddOpenAiMultipartString(content, "size", request.Size);

        content.Add(CreateOpenAiImageFileContent(file), "image", GetOpenAiImageFilename(file, 0));

        return new HttpRequestMessage(HttpMethod.Post, "v1/images/variations")
        {
            Content = content
        };
    }

    private static void AddOpenAiImageCommonOptions(
        Dictionary<string, object?> payload,
        ImageRequest request,
        string? quality,
        List<object> warnings,
        OpenAiImageOperation operation)
    {
        if (request.N.HasValue)
            payload["n"] = request.N.Value;

        var size = ResolveOpenAiImageSize(request, warnings, operation);
        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        if (!string.IsNullOrWhiteSpace(quality))
            payload["quality"] = quality;
    }

    private static string? ResolveOpenAiImageSize(ImageRequest request, List<object> warnings, OpenAiImageOperation operation)
    {
        if (!string.IsNullOrWhiteSpace(request.Size))
            return request.Size;

        if (string.IsNullOrWhiteSpace(request.AspectRatio))
            return null;

        if (operation == OpenAiImageOperation.Variation)
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

    private static Dictionary<string, object?> ToOpenAiImageReference(ImageFile file)
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
            ["image_url"] = NormalizeOpenAiImageInput(file)
        };
    }

    private static string NormalizeOpenAiImageInput(ImageFile file)
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

    private static HttpContent CreateOpenAiImageFileContent(ImageFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OpenAI image variations require an uploaded image file. URL inputs are not supported for variations.");

        var rawData = file.Data.RemoveDataUrlPrefix();
        var bytes = Convert.FromBase64String(rawData);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType);
        return content;
    }

    private static string GetOpenAiImageFilename(ImageFile file, int index)
    {
        var extension = file.MediaType?.ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Jpeg => "jpg",
            "image/webp" => "webp",
            "image/gif" => "gif",
            _ => "png"
        };

        return $"image-{index}.{extension}";
    }

    private static void AddOpenAiMultipartString(MultipartFormDataContent content, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        content.Add(new StringContent(value, Encoding.UTF8), name);
    }

    private static List<string> ExtractOpenAiImages(JsonElement root)
    {
        List<string> images = [];

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        var outputFormat = ReadOpenAiImageString(root, "output_format");
        var mediaType = OpenAiImageMediaTypeFromFormat(outputFormat);

        foreach (var image in data.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object)
                continue;

            if (image.TryGetProperty("b64_json", out var b64Json) && b64Json.ValueKind == JsonValueKind.String)
            {
                var value = b64Json.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    images.Add(NormalizeOpenAiImageOutput(value, mediaType));
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

    private static ImageUsageData? ExtractOpenAiImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var usageData = new ImageUsageData
        {
            InputTokens = ReadOpenAiImageInt(usage, "input_tokens"),
            OutputTokens = ReadOpenAiImageInt(usage, "output_tokens"),
            TotalTokens = ReadOpenAiImageInt(usage, "total_tokens"),
        };

        if (!usageData.InputTokens.HasValue
            && !usageData.OutputTokens.HasValue
            && !usageData.TotalTokens.HasValue)
        {
            return null;
        }

        return usageData;
    }

    private static Dictionary<string, JsonElement>? BuildOpenAiImageProviderMetadata(
        JsonElement root,
        string providerKey,
        decimal? cost)
    {
        var providerMetadata = new Dictionary<string, JsonElement>();

        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            providerMetadata[providerKey] = JsonSerializer.SerializeToElement(new
            {
                usage = usageEl.Clone()
            }, JsonSerializerOptions.Web);
        }

        if (cost is not null)
        {
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(new
            {
                cost
            }, JsonSerializerOptions.Web);
        }

        return providerMetadata.Count == 0 ? null : providerMetadata;
    }

    private static decimal? CalculateOpenAiImageCost(string model, JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        if (!OpenAiImagePricing.TryGetValue(NormalizeOpenAiImageModel(model), out var pricing))
            return null;

        usage.TryGetProperty("input_tokens_details", out var inputDetails);
        usage.TryGetProperty("output_tokens_details", out var outputDetails);

        var inputTokens = ReadOpenAiImageInt(usage, "input_tokens") ?? 0;
        var outputTokens = ReadOpenAiImageInt(usage, "output_tokens") ?? 0;

        var imageInputTokens = inputDetails.ValueKind == JsonValueKind.Object
            ? ReadOpenAiImageInt(inputDetails, "image_tokens") ?? 0
            : 0;
        var textInputTokens = inputDetails.ValueKind == JsonValueKind.Object
            ? ReadOpenAiImageInt(inputDetails, "text_tokens") ?? 0
            : inputTokens;

        if (inputDetails.ValueKind == JsonValueKind.Object
            && imageInputTokens == 0
            && textInputTokens == 0
            && inputTokens > 0)
        {
            textInputTokens = inputTokens;
        }

        var cachedInputTokens = inputDetails.ValueKind == JsonValueKind.Object
            ? ReadOpenAiImageInt(inputDetails, "cached_tokens") ?? 0
            : 0;
        var cachedImageInputTokens = inputDetails.ValueKind == JsonValueKind.Object
            ? ReadOpenAiImageInt(inputDetails, "cached_image_tokens") ?? 0
            : 0;
        var cachedTextInputTokens = inputDetails.ValueKind == JsonValueKind.Object
            ? ReadOpenAiImageInt(inputDetails, "cached_text_tokens") ?? Math.Max(0, cachedInputTokens - cachedImageInputTokens)
            : 0;

        cachedImageInputTokens = Math.Min(cachedImageInputTokens, imageInputTokens);
        cachedTextInputTokens = Math.Min(cachedTextInputTokens, textInputTokens);

        var uncachedImageInputTokens = Math.Max(0, imageInputTokens - cachedImageInputTokens);
        var uncachedTextInputTokens = Math.Max(0, textInputTokens - cachedTextInputTokens);

        var imageOutputTokens = outputDetails.ValueKind == JsonValueKind.Object
            ? ReadOpenAiImageInt(outputDetails, "image_tokens") ?? 0
            : outputTokens;
        var textOutputTokens = outputDetails.ValueKind == JsonValueKind.Object
            ? ReadOpenAiImageInt(outputDetails, "text_tokens") ?? 0
            : 0;

        if (outputDetails.ValueKind == JsonValueKind.Object
            && imageOutputTokens == 0
            && textOutputTokens == 0
            && outputTokens > 0)
        {
            imageOutputTokens = outputTokens;
        }

        var cost = 0m;
        cost += uncachedImageInputTokens * pricing.ImageInputPerMillion / 1_000_000m;
        cost += cachedImageInputTokens * pricing.ImageCachedInputPerMillion / 1_000_000m;
        cost += uncachedTextInputTokens * pricing.TextInputPerMillion / 1_000_000m;
        cost += cachedTextInputTokens * pricing.TextCachedInputPerMillion / 1_000_000m;
        cost += imageOutputTokens * pricing.ImageOutputPerMillion / 1_000_000m;

        if (pricing.TextOutputPerMillion is { } textOutputPrice)
            cost += textOutputTokens * textOutputPrice / 1_000_000m;

        return decimal.Round(cost, 12, MidpointRounding.AwayFromZero);
    }

    private static string NormalizeOpenAiImageOutput(string value, string mediaType)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return value.ToDataUrl(mediaType);
    }

    private static string OpenAiImageMediaTypeFromFormat(string? outputFormat)
        => outputFormat?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };

    private static bool IsGptOpenAiImageModel(string model)
    {
        var normalized = NormalizeOpenAiImageModel(model);
        return normalized.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "chatgpt-image-latest", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOpenAiImageModel(string model)
    {
        var normalized = model.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? model;

        if (normalized.StartsWith("gpt-image-2-", StringComparison.OrdinalIgnoreCase))
            return "gpt-image-2";

        return normalized;
    }

    private static string? ReadOpenAiImageString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? ReadOpenAiImageInt(JsonElement element, string propertyName)
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

    private enum OpenAiImageOperation
    {
        Generation,
        Variation,
        Edit
    }

    private sealed record OpenAiImageTokenPricing(
        decimal ImageInputPerMillion,
        decimal ImageCachedInputPerMillion,
        decimal ImageOutputPerMillion,
        decimal TextInputPerMillion,
        decimal TextCachedInputPerMillion,
        decimal? TextOutputPerMillion);
}
