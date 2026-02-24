using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BlackForestLabs;

public partial class BlackForestLabsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record BflResult(string Status, JsonElement Root, string Raw);

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var model = request.Model;
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var isFill = string.Equals(model, "flux-pro-1.0-fill", StringComparison.OrdinalIgnoreCase);
        var isExpand = string.Equals(model, "flux-pro-1.0-expand", StringComparison.OrdinalIgnoreCase);

        if (!isFill && request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (!isFill && !isExpand && string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var payload = BuildPayload(request, model, warnings);
        MergeProviderOptions(payload, request);

        var endpoint = ResolveEndpoint(model);
        var submitJson = JsonSerializer.Serialize(payload, JsonOptions);

        using var submitReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(submitJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var submitResp = await _client.SendAsync(submitReq, cancellationToken);
        var submitRaw = await submitResp.Content.ReadAsStringAsync(cancellationToken);
        if (!submitResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"BlackForestLabs submit failed ({(int)submitResp.StatusCode}): {submitRaw}");

        using var submitDoc = JsonDocument.Parse(submitRaw);
        var submitRoot = submitDoc.RootElement.Clone();

        var taskId = TryGetString(submitRoot, "id") ?? throw new InvalidOperationException("BlackForestLabs response missing id.");

        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollResultAsync(taskId, ct),
            isTerminal: r => IsTerminalStatus(r.Status),
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!string.Equals(final.Status, "Ready", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"BlackForestLabs task failed (status={final.Status}, id={taskId}).");

        var outputFormat = TryGetOutputFormat(payload) ?? "jpeg";
        var mime = MapOutputFormatToMimeType(outputFormat);

        var images = await ExtractImagesAsync(final.Root, mime, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("BlackForestLabs result did not contain any images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = final.Root.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new Dictionary<string, object?>
                {
                    ["submit"] = submitRoot,
                    ["poll"] = final.Root.Clone()
                }
            }
        };
    }

    private static Dictionary<string, object?> BuildPayload(ImageRequest request, string model, List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt
        };

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        var normalizedSize = request.Size?.Replace(":", "x", StringComparison.OrdinalIgnoreCase);
        var width = string.IsNullOrWhiteSpace(normalizedSize) ? null : new ImageRequest { Size = normalizedSize }.GetImageWidth();
        var height = string.IsNullOrWhiteSpace(normalizedSize) ? null : new ImageRequest { Size = normalizedSize }.GetImageHeight();

        if ((width is null || height is null) && !string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            var inferred = InferSizeForModel(model, request.AspectRatio);
            if (inferred is not null)
            {
                width ??= inferred.Value.width;
                height ??= inferred.Value.height;
            }
        }

        if (model is "flux-pro-1.1" or "flux-dev")
        {
            if (width is not null)
                payload["width"] = width;
            if (height is not null)
                payload["height"] = height;
            AddImagePrompt(payload, request.Files, warnings, maxImages: 1, fieldName: "image_prompt");
            return payload;
        }

        if (model is "flux-pro-1.1-ultra")
        {
            var aspectRatio = request.AspectRatio ?? AspectRatioFromSize(normalizedSize);
            if (!string.IsNullOrWhiteSpace(aspectRatio))
                payload["aspect_ratio"] = aspectRatio;
            AddImagePrompt(payload, request.Files, warnings, maxImages: 1, fieldName: "image_prompt");
            return payload;
        }

        if (model is "flux-pro-1.0-fill")
        {
            var image = request.Files?.FirstOrDefault() ?? throw new ArgumentException("Input image is required.", nameof(request));
            payload["image"] = NormalizeImageData(image);
            if (request.Mask is not null)
                payload["mask"] = NormalizeImageData(request.Mask);
            return payload;
        }

        if (model is "flux-pro-1.0-expand")
        {
            var image = request.Files?.FirstOrDefault() ?? throw new ArgumentException("Input image is required.", nameof(request));
            payload["image"] = NormalizeImageData(image);
            return payload;
        }

        if (model is "flux-kontext-pro" or "flux-kontext-max")
        {
            var aspectRatio = request.AspectRatio ?? AspectRatioFromSize(normalizedSize);
            if (!string.IsNullOrWhiteSpace(aspectRatio))
                payload["aspect_ratio"] = aspectRatio;
            AddInputImages(payload, request.Files, warnings, maxImages: 4);
            return payload;
        }

        if (IsFlux2Model(model))
        {
            if (width is not null)
                payload["width"] = width;
            if (height is not null)
                payload["height"] = height;

            var maxImages = model is "flux-2-klein-4b" or "flux-2-klein-9b" ? 4 : 8;
            AddInputImages(payload, request.Files, warnings, maxImages);
            return payload;
        }

        throw new NotSupportedException($"BlackForestLabs image model '{request.Model}' is not supported.");
    }

    private static (int width, int height)? InferSizeForModel(string model, string aspectRatio)
    {
        if (model is "flux-pro-1.1" or "flux-dev")
        {
            return aspectRatio.InferSizeFromAspectRatio(minWidth: 256, maxWidth: 1440, minHeight: 256, maxHeight: 1440);
        }

        if (IsFlux2Model(model))
        {
            return aspectRatio.InferSizeFromAspectRatio(minWidth: 64, maxWidth: 2048, minHeight: 64, maxHeight: 2048);
        }

        return aspectRatio.InferSizeFromAspectRatio();
    }

    private static void AddImagePrompt(
        Dictionary<string, object?> payload,
        IEnumerable<ImageFile>? files,
        List<object> warnings,
        int maxImages,
        string fieldName)
    {
        if (files is null)
            return;

        var list = files.ToList();
        if (list.Count == 0)
            return;

        if (list.Count > maxImages)
        {
            warnings.Add(new { type = "unsupported", feature = "files", details = $"Only {maxImages} input image(s) supported; used files[0]." });
        }

        payload[fieldName] = NormalizeImageData(list[0]);
    }

    private static void AddInputImages(Dictionary<string, object?> payload, IEnumerable<ImageFile>? files, List<object> warnings, int maxImages)
    {
        if (files is null)
            return;

        var list = files.ToList();
        if (list.Count == 0)
            return;

        if (list.Count > maxImages)
        {
            warnings.Add(new { type = "unsupported", feature = "files", details = $"Only {maxImages} input images supported; used first {maxImages}." });
        }

        for (var i = 0; i < Math.Min(list.Count, maxImages); i++)
        {
            var key = i == 0 ? "input_image" : $"input_image_{i + 1}";
            payload[key] = NormalizeImageData(list[i]);
        }
    }

    private static void MergeProviderOptions(Dictionary<string, object?> payload, ImageRequest request)
    {
        var options = request.GetProviderMetadata<JsonElement>(nameof(BlackForestLabs).ToLowerInvariant());
        if (options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in options.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            payload[prop.Name] = prop.Value;
        }
    }

    private async Task<BflResult> PollResultAsync(string taskId, CancellationToken cancellationToken)
    {
        var url = $"v1/get_result?id={Uri.EscapeDataString(taskId)}";
        using var pollResp = await _client.GetAsync(url, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"BlackForestLabs polling failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = TryGetString(root, "status") ?? "unknown";

        return new BflResult(status, root, pollRaw);
    }

    private static bool IsTerminalStatus(string? status)
        => status is not null && (status is "Ready" or "Error" or "Request Moderated" or "Content Moderated" or "Task not found");

    private async Task<List<string>> ExtractImagesAsync(JsonElement root, string defaultMime, CancellationToken cancellationToken)
    {
        var result = root.TryGetProperty("result", out var resultEl) ? resultEl : root;
        var dataUrls = new List<string>();
        var urls = new List<string>();

        Visit(result, null);

        foreach (var url in urls)
        {
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataUrls.Add(url);
                continue;
            }

            dataUrls.Add(await DownloadAsDataUrlAsync(url, cancellationToken));
        }

        return dataUrls;

        void Visit(JsonElement element, string? nameHint)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var value = element.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                        return;

                    if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    {
                        urls.Add(value);
                        return;
                    }

                    if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https"))
                    {
                        urls.Add(value);
                        return;
                    }

                    if (LooksLikeBase64Field(nameHint))
                    {
                        dataUrls.Add(value.ToDataUrl(defaultMime));
                    }
                    return;

                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                        Visit(prop.Value, prop.Name);
                    return;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Visit(item, nameHint);
                    return;
            }
        }
    }

    private async Task<string> DownloadAsDataUrlAsync(string url, CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync(url, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"BlackForestLabs image download failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mime = resp.Content.Headers.ContentType?.MediaType ?? GuessImageMediaType(url) ?? MediaTypeNames.Image.Jpeg;
        return Convert.ToBase64String(bytes).ToDataUrl(mime);
    }

    private static string? GuessImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var lower = url.Trim().ToLowerInvariant();
        if (lower.Contains(".png")) return MediaTypeNames.Image.Png;
        if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return MediaTypeNames.Image.Jpeg;
        if (lower.Contains(".webp")) return "image/webp";
        if (lower.Contains(".gif")) return MediaTypeNames.Image.Gif;
        if (lower.Contains(".bmp")) return "image/bmp";
        if (lower.Contains(".avif")) return "image/avif";

        return null;
    }

    private static bool LooksLikeBase64Field(string? nameHint)
        => !string.IsNullOrWhiteSpace(nameHint)
           && (nameHint.Contains("b64", StringComparison.OrdinalIgnoreCase)
               || nameHint.Contains("base64", StringComparison.OrdinalIgnoreCase)
               || nameHint.Contains("b64_json", StringComparison.OrdinalIgnoreCase));

    private static string ResolveEndpoint(string model)
        => model switch
        {
            "flux-pro-1.1" => "v1/flux-pro-1.1",
            "flux-dev" => "v1/flux-dev",
            "flux-pro-1.1-ultra" => "v1/flux-pro-1.1-ultra",
            "flux-pro-1.0-fill" => "v1/flux-pro-1.0-fill",
            "flux-pro-1.0-expand" => "v1/flux-pro-1.0-expand",
            "flux-kontext-pro" => "v1/flux-kontext-pro",
            "flux-kontext-max" => "v1/flux-kontext-max",
            "flux-2-max" => "v1/flux-2-max",
            "flux-2-klein-9b" => "v1/flux-2-klein-9b",
            "flux-2-klein-4b" => "v1/flux-2-klein-4b",
            "flux-2-pro" => "v1/flux-2-pro",
            "flux-2-flex" => "v1/flux-2-flex",
            _ => throw new NotSupportedException($"BlackForestLabs image model '{model}' is not supported.")
        };

    private static bool IsFlux2Model(string model)
        => model is "flux-2-max" or "flux-2-klein-9b" or "flux-2-klein-4b" or "flux-2-pro" or "flux-2-flex";

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string MapOutputFormatToMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "png" => MediaTypeNames.Image.Png,
            "jpg" => MediaTypeNames.Image.Jpeg,
            "jpeg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Jpeg
        };

    private static string? TryGetOutputFormat(Dictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("output_format", out var value) || value is null)
            return null;

        return value switch
        {
            string s => s,
            JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
            _ => null
        };
    }

    private static string NormalizeImageData(ImageFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            return file.Data;

        return file.Data.RemoveDataUrlPrefix();
    }

    private static string? AspectRatioFromSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size))
            return null;

        var parts = size.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return null;

        if (!int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
            return null;

        var gcd = Gcd(width, height);
        return gcd == 0 ? null : $"{width / gcd}:{height / gcd}";
    }

    private static int Gcd(int a, int b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
        {
            var t = b;
            b = a % b;
            a = t;
        }
        return a;
    }
}
