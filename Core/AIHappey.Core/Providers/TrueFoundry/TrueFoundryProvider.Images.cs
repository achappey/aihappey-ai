using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.TrueFoundry;

public partial class TrueFoundryProvider
{
    private async Task<ImageResponse> TrueFoundryImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var files = request.Files?.Where(file => file is not null).ToList() ?? [];
        var warnings = new List<object>();

        if (files.Count != 1 && string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required for TrueFoundry image generation and image edits.", nameof(request));

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed", details = "TrueFoundry image endpoints do not document seed." });

        var operation = ResolveTrueFoundryImageOperation(files.Count);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var now = DateTime.UtcNow;

        using var httpRequest = operation switch
        {
            TrueFoundryImageOperation.Generation => BuildTrueFoundryImageGenerationRequest(request, metadata, warnings),
            TrueFoundryImageOperation.Variation => BuildTrueFoundryImageVariationRequest(request, files[0], metadata, warnings),
            TrueFoundryImageOperation.Edit => BuildTrueFoundryImageEditRequest(request, files, metadata, warnings),
            _ => throw new NotSupportedException($"Unsupported TrueFoundry image operation {operation}.")
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"TrueFoundry image request failed ({(int)response.StatusCode})."
                : $"TrueFoundry image request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var images = await ExtractTrueFoundryImagesAsync(root, metadata, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("TrueFoundry image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractTrueFoundryImageUsage(root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = ReadTrueFoundryUnixTimestamp(root, "created") ?? now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private TrueFoundryImageOperation ResolveTrueFoundryImageOperation(int fileCount)
        => fileCount switch
        {
            0 => TrueFoundryImageOperation.Generation,
            1 => TrueFoundryImageOperation.Variation,
            _ => TrueFoundryImageOperation.Edit
        };

    private HttpRequestMessage BuildTrueFoundryImageGenerationRequest(ImageRequest request, JsonElement metadata, List<object> warnings)
    {
        var payload = TrueFoundryJsonObjectToDictionary(metadata);

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        AddTrueFoundryImageCommonJsonOptions(payload, request, warnings, TrueFoundryImageOperation.Generation);

        return new HttpRequestMessage(HttpMethod.Post, "images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, TrueFoundryJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
    }

    private HttpRequestMessage BuildTrueFoundryImageEditRequest(ImageRequest request, IReadOnlyList<ImageFile> files, JsonElement metadata, List<object> warnings)
    {
        var form = new MultipartFormDataContent();

        AddTrueFoundryMetadataFormFields(form, metadata, "model", "image", "prompt");
        AddTrueFoundryMultipartString(form, "model", request.Model);
        AddTrueFoundryMultipartString(form, "prompt", request.Prompt);
        AddTrueFoundryImageCommonFormOptions(form, request, warnings, TrueFoundryImageOperation.Edit);

        for (var i = 0; i < files.Count; i++)
            form.Add(CreateTrueFoundryImageFileContent(files[i], "TrueFoundry image edits require uploaded image bytes. URL image inputs are not supported by this adapter for edits."), "image", GetTrueFoundryImageFilename(files[i], i));

        if (request.Mask is not null)
            form.Add(CreateTrueFoundryImageFileContent(request.Mask, "TrueFoundry image edits require uploaded mask bytes. URL mask inputs are not supported by this adapter for edits."), "mask", GetTrueFoundryImageFilename(request.Mask, 0, "mask"));

        return new HttpRequestMessage(HttpMethod.Post, "images/edits")
        {
            Content = form
        };
    }

    private HttpRequestMessage BuildTrueFoundryImageVariationRequest(ImageRequest request, ImageFile file, JsonElement metadata, List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Prompt))
            warnings.Add(new { type = "unsupported", feature = "prompt", details = "TrueFoundry image variations do not document prompt. Prompt was omitted." });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", details = "TrueFoundry image variations do not document mask. Mask was omitted." });

        var form = new MultipartFormDataContent();

        AddTrueFoundryMetadataFormFields(form, metadata, "model", "image");
        AddTrueFoundryMultipartString(form, "model", request.Model);
        AddTrueFoundryImageCommonFormOptions(form, request, warnings, TrueFoundryImageOperation.Variation);
        form.Add(CreateTrueFoundryImageFileContent(file, "TrueFoundry image variations require uploaded image bytes. URL image inputs are not supported for variations."), "image", GetTrueFoundryImageFilename(file, 0));

        return new HttpRequestMessage(HttpMethod.Post, "images/variations")
        {
            Content = form
        };
    }

    private void AddTrueFoundryImageCommonJsonOptions(Dictionary<string, object?> payload, ImageRequest request, List<object> warnings, TrueFoundryImageOperation operation)
    {
        if (request.N.HasValue)
            payload["n"] = request.N.Value;

        var size = ResolveTrueFoundryImageSize(request, warnings, operation);
        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;
    }

    private void AddTrueFoundryImageCommonFormOptions(MultipartFormDataContent form, ImageRequest request, List<object> warnings, TrueFoundryImageOperation operation)
    {
        if (request.N.HasValue)
            AddTrueFoundryMultipartString(form, "n", request.N.Value.ToString(CultureInfo.InvariantCulture));

        var size = ResolveTrueFoundryImageSize(request, warnings, operation);
        AddTrueFoundryMultipartString(form, "size", size);
    }

    private string? ResolveTrueFoundryImageSize(ImageRequest request, List<object> warnings, TrueFoundryImageOperation operation)
    {
        if (!string.IsNullOrWhiteSpace(request.Size))
            return request.Size;

        if (string.IsNullOrWhiteSpace(request.AspectRatio))
            return null;

        if (operation == TrueFoundryImageOperation.Variation)
        {
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "TrueFoundry image variations document size, not aspectRatio. aspectRatio was omitted." });
            return null;
        }

        var size = request.AspectRatio.Trim().ToLowerInvariant() switch
        {
            "1:1" => "1024x1024",
            "2:3" or "3:4" or "9:16" => "1024x1536",
            "3:2" or "4:3" or "16:9" => "1536x1024",
            _ => null
        };

        if (size is null)
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = $"Requested aspect ratio {request.AspectRatio} could not be mapped to a TrueFoundry image size." });

        return size;
    }

    private async Task<List<string>> ExtractTrueFoundryImagesAsync(JsonElement root, JsonElement metadata, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        var fallbackMediaType = TrueFoundryImageMediaTypeFromFormat(
            TrueFoundryTryGetString(root, "output_format", "format", "mime_type")
            ?? TrueFoundryTryGetString(metadata, "output_format", "outputFormat", "format", "mime_type"));

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var mediaType = TrueFoundryImageMediaTypeFromFormat(
                TrueFoundryTryGetString(item, "output_format", "format", "mime_type")
                ?? fallbackMediaType);

            if (item.TryGetProperty("b64_json", out var b64El) && b64El.ValueKind == JsonValueKind.String)
            {
                var b64 = b64El.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    images.Add(b64.ToDataUrl(mediaType));

                continue;
            }

            if (!item.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;

            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            if (url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(url);
                continue;
            }

            using var imageResponse = await _client.GetAsync(url, cancellationToken);
            var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!imageResponse.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"Failed to download TrueFoundry image from returned URL ({(int)imageResponse.StatusCode}).");

            var downloadedMediaType = imageResponse.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(downloadedMediaType) || !downloadedMediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                downloadedMediaType = mediaType;

            images.Add(Convert.ToBase64String(bytes).ToDataUrl(downloadedMediaType));
        }

        return [.. images.Distinct(StringComparer.Ordinal)];
    }

    private ImageUsageData? ExtractTrueFoundryImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var usageData = new ImageUsageData
        {
            InputTokens = TrueFoundryTryGetInt(usage, "input_tokens", "inputTokens"),
            OutputTokens = TrueFoundryTryGetInt(usage, "output_tokens", "outputTokens"),
            TotalTokens = TrueFoundryTryGetInt(usage, "total_tokens", "totalTokens")
        };

        return usageData.InputTokens.HasValue || usageData.OutputTokens.HasValue || usageData.TotalTokens.HasValue
            ? usageData
            : null;
    }

    private HttpContent CreateTrueFoundryImageFileContent(ImageFile file, string urlErrorMessage)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(urlErrorMessage);

        var bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix());
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType);
        return content;
    }

    private string GetTrueFoundryImageFilename(ImageFile file, int index, string prefix = "image")
    {
        var extension = file.MediaType?.Trim().ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Jpeg => "jpg",
            "image/webp" => "webp",
            "image/gif" => "gif",
            _ => "png"
        };

        return $"{prefix}-{index}.{extension}";
    }

    private string TrueFoundryImageMediaTypeFromFormat(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" or MediaTypeNames.Image.Jpeg => MediaTypeNames.Image.Jpeg,
            "webp" or "image/webp" => "image/webp",
            "gif" or "image/gif" => "image/gif",
            _ => MediaTypeNames.Image.Png
        };
}

internal enum TrueFoundryImageOperation
{
    Generation,
    Variation,
    Edit
}
