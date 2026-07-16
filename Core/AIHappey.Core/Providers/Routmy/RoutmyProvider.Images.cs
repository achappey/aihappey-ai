using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Routmy;

public partial class RoutmyProvider
{
    private static readonly JsonSerializerOptions RoutmyMediaJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> ImageRequestRoutmy(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var hasInputImages = request.Files?.Any() == true;
        var endpoint = hasInputImages ? "v1/images/edits" : "v1/images/generations";
        var payload = BuildRoutmyImagePayload(request, hasInputImages);
        var root = await SendRoutmyMediaJsonAsync(endpoint, payload, "image", cancellationToken);
        var images = await ExtractRoutmyImagesAsync(root, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("Routmy image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractRoutmyImageUsage(root),
            ProviderMetadata = BuildRoutmyMediaProviderMetadata(payload, root),
            Response = new()
            {
                Timestamp = ResolveRoutmyCreatedTimestamp(root) ?? now,
                ModelId = ResolveRoutmyResponseModel(root, request.Model).ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> BuildRoutmyImagePayload(ImageRequest request, bool hasInputImages)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        if (request.N is not null)
            payload["n"] = request.N;

        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            payload["image_config"] = new Dictionary<string, object?>
            {
                ["aspect_ratio"] = request.AspectRatio
            };
        }

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        if (hasInputImages)
            payload["images"] = request.Files!.Select(ToRoutmyImageObject).ToArray();

        if (request.Mask is not null)
            payload["mask"] = ToRoutmyImageObject(request.Mask);

        MergeRoutmyProviderOptions(payload, request.ProviderOptions, RoutmyImageProtectedKeys);

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        return payload;
    }

    private async Task<List<string>> ExtractRoutmyImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        List<string> images = [];

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in data.EnumerateArray())
        {
            var b64 = TryGetRoutmyString(item, "b64_json")
                ?? TryGetRoutmyString(item, "base64")
                ?? TryGetRoutmyString(item, "data");

            if (!string.IsNullOrWhiteSpace(b64))
            {
                images.Add(NormalizeRoutmyImageBase64(b64));
                continue;
            }

            var url = TryGetRoutmyString(item, "url")
                ?? TryGetRoutmyNestedString(item, "image_url", "url");

            if (string.IsNullOrWhiteSpace(url))
                continue;

            var downloaded = await TryFetchRoutmyAsBase64Async(url, cancellationToken);
            images.Add(downloaded is null
                ? url
                : downloaded.Value.Base64.ToDataUrl(downloaded.Value.MediaType));
        }

        return images;
    }

    private static ImageUsageData? ExtractRoutmyImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        return new ImageUsageData
        {
            InputTokens = TryGetRoutmyInt(usage, "input_tokens") ?? TryGetRoutmyInt(usage, "inputTokens"),
            OutputTokens = TryGetRoutmyInt(usage, "output_tokens") ?? TryGetRoutmyInt(usage, "outputTokens"),
            TotalTokens = TryGetRoutmyInt(usage, "total_tokens") ?? TryGetRoutmyInt(usage, "totalTokens")
        };
    }

    private static string NormalizeRoutmyImageBase64(string value)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value;

        return value.ToDataUrl(MediaTypeNames.Image.Png);
    }

    private static object ToRoutmyImageObject(ImageFile file)
    {
        var value = file.Data;
        if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            value = value.ToDataUrl(string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType);
        }

        return new Dictionary<string, object?>
        {
            ["image_url"] = value
        };
    }

    private static readonly HashSet<string> RoutmyImageProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "model",
        "prompt"
    };
}
