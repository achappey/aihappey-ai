using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.DeepInfra;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string? ResolveSize(ImageRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Size))
            return req.Size;

        if (!string.IsNullOrWhiteSpace(req.AspectRatio))
        {
            var inferred = req.AspectRatio.InferSizeFromAspectRatio();
            if (inferred is not null)
                return $"{inferred.Value.width}x{inferred.Value.height}";
        }

        return null;
    }

    private static ByteArrayContent ToImageContent(ImageFile file)
    {
        var bytes = Convert.FromBase64String(file.Data);

        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(file.MediaType);

        return content;
    }

    private async Task<ImageResponse> OpenAIImageEditAsync(
        ImageRequest req,
        CancellationToken ct)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();

        form.Add(new StringContent(req.Model), "model");
        form.Add(new StringContent(req.Prompt), "prompt");
        form.Add(new StringContent((req.N ?? 1).ToString()), "n");

        var size = ResolveSize(req);
        if (!string.IsNullOrWhiteSpace(size))
            form.Add(new StringContent(size), "size");

        // REQUIRED image
        var image = req.Files?.First()
            ?? throw new InvalidOperationException("Image edit requires an input image.");

        form.Add(
            ToImageContent(image),
            "image",
            "image"
        );

        // OPTIONAL mask
        if (req.Mask is not null)
        {
            form.Add(
                ToImageContent(req.Mask),
                "mask",
                "mask"
            );
        }

        using var resp = await _client.PostAsync(
            "v1/openai/images/edits",
            form,
            ct);

        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var images = await ExtractOpenAIImagesAsDataUrlsAsync(raw, ct);
        if (images.Count == 0)
            throw new Exception("DeepInfra returned no images.");

        return new ImageResponse
        {
            Images = images,
            Response = new()
            {
                Timestamp = now,
                ModelId = req.Model,
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }


    private async Task<ImageResponse> ImageEditAsync(
        ImageRequest req,
        CancellationToken ct)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var metadata = req.GetProviderMetadata<DeepInfraImageProviderMetadata>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = req.Prompt,
            ["num_results"] = req.N ?? 1,
        };

        if (!string.IsNullOrEmpty(req.AspectRatio))
        {
            payload["aspect_ratio"] = req.AspectRatio;
        }

        if (!string.IsNullOrEmpty(req.Size))
        {
            payload["resolution"] = req.Size;
        }

        if (req.Seed is not null)
        {
            payload["seed"] = req.Seed;
        }

        if (!string.IsNullOrEmpty(metadata?.Bria?.Speed))
        {
            payload["speed"] = metadata?.Bria?.Speed;
        }

        if (!string.IsNullOrEmpty(metadata?.Bria?.StructuredPrompt))
        {
            payload["structured_prompt"] = metadata?.Bria?.StructuredPrompt;
        }

        if (req.Files?.Any() == true)
        {
            var file = req.Files.First();
            payload["image"] = file.ToDataUrl();
        }

        var json = JsonSerializer.Serialize(payload, ImageJson);

        using var resp = await _client.PostAsync(
            $"v1/inference/{req.Model}",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            ct);

        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var images = await ExtractImagesAsDataUrlsAsync(raw, ct);
        if (images.Count == 0)
            throw new Exception("DeepInfra returned no images.");

        return new ImageResponse
        {
            Images = images,
            Response = new()
            {
                Timestamp = now,
                ModelId = req.Model,
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }

    private async Task<ImageResponse> ImageGenerateAsync(
        ImageRequest req,
        CancellationToken ct)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = req.Prompt,
            ["num_results"] = req.N ?? 1,
        };

        if (!string.IsNullOrEmpty(req.AspectRatio))
        {
            payload["aspect_ratio"] = req.AspectRatio;
        }

        if (req.Seed is not null)
        {
            payload["seed"] = req.Seed;
        }

        var json = JsonSerializer.Serialize(payload, ImageJson);

        using var resp = await _client.PostAsync(
            $"v1/inference/{req.Model}",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            ct);

        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var images = await ExtractImagesAsDataUrlsAsync(raw, ct);
        if (images.Count == 0)
            throw new Exception("DeepInfra returned no images.");

        return new ImageResponse
        {
            Images = images,
            Response = new()
            {
                Timestamp = now,
                ModelId = req.Model,
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }


    public async Task<ImageResponse> ImageRequest(
    ImageRequest imageRequest,
    CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imageRequest);

        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var isOAIEdit = imageRequest.Model.EndsWith("Qwen/Qwen-Image-Edit")
            || imageRequest.Model.EndsWith("black-forest-labs/FLUX.1-Kontext-dev");
        var isNativeEdit = imageRequest.Model.EndsWith("Bria/fibo");

        return isOAIEdit
            ? await OpenAIImageEditAsync(imageRequest, cancellationToken)
            : isNativeEdit ? await ImageEditAsync(imageRequest, cancellationToken)
            : await ImageGenerateAsync(imageRequest, cancellationToken);
    }

    private async Task<List<string>> ExtractImagesAsDataUrlsAsync(
        string rawJson,
        CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(rawJson);

        HttpClient? client = null;

        if (!doc.RootElement.TryGetProperty("images", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return [];

        List<string> images = [];

        foreach (var item in data.EnumerateArray())
        {
            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            // Already a data URL → keep
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(value);
                continue;
            }

            // HTTPS URL → download + convert
            if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                client ??= httpClientFactory.CreateClient();

                using var resp = await client.GetAsync(value, ct);
                resp.EnsureSuccessStatusCode();

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                var mediaType =
                    resp.Content.Headers.ContentType?.MediaType ?? "image/png";

                var base64 = Convert.ToBase64String(bytes);
                images.Add($"data:{mediaType};base64,{base64}");
            }
        }

        return images;
    }

    private async Task<List<string>> ExtractOpenAIImagesAsDataUrlsAsync(string rawJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        List<string> images = [];

        foreach (var item in data.EnumerateArray())
        {
            // OpenAI-like responses may contain b64_json or url.
            if (item.TryGetProperty("b64_json", out var b64Prop))
            {
                var b64 = b64Prop.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    images.Add(b64.ToDataUrl(MediaTypeNames.Image.Png));
                continue;
            }

            if (item.TryGetProperty("url", out var urlProp))
            {
                var url = urlProp.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var bytes = await _client.GetByteArrayAsync(url, ct);
                var mime = GuessImageMimeTypeFromUrl(url);
                images.Add(Convert.ToBase64String(bytes).ToDataUrl(mime));
            }
        }

        return images;
    }

    private static string GuessImageMimeTypeFromUrl(string url)
    {
        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Jpeg;

        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";

        if (url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Gif;

        return MediaTypeNames.Image.Png;
    }
}