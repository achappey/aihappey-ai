using AIHappey.Common.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.Venice;

public partial class VeniceProvider
{
    private async Task<ImageResponse> ImageRequestVenice(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var files = request.Files?.ToList() ?? [];

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", details = "Venice image endpoints do not expose a dedicated mask field in this provider mapping." });

        var endpoint = ResolveImageEndpoint(request.Model, files.Count, warnings);
        EnsureRequiredInput(endpoint, request, files, metadata);

        var useMultipart = endpoint != "v1/image/generate" && ShouldUseMultipart(files, metadata);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (useMultipart)
            httpRequest.Content = BuildMultipartContent(endpoint, request, files, metadata, warnings);
        else
            httpRequest.Content = BuildJsonContent(endpoint, request, files, metadata, warnings);

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        return await ParseImageResponseAsync(response, endpoint, request.Model, warnings, now, cancellationToken);
    }

    private static string ResolveImageEndpoint(string model, int incomingImageCount, List<object> warnings)
    {
        var normalizedModel = model;

        if (string.Equals(normalizedModel, "upscale", StringComparison.OrdinalIgnoreCase))
            return "v1/image/upscale";

        if (string.Equals(normalizedModel, "background-remove", StringComparison.OrdinalIgnoreCase))
            return "v1/image/background-remove";

        if (IsZImageTurboModel(model))
        {
            if (incomingImageCount > 0)
                warnings.Add(new { type = "ignored", feature = "files", details = "Model z-image-turbo is forced to /v1/image/generate. Incoming images were ignored." });

            return "v1/image/generate";
        }

        if (incomingImageCount <= 0)
            throw new InvalidOperationException("Venice image edit models require at least one input image. Use model z-image-turbo for text-to-image generation.");

        if (incomingImageCount == 1)
            return "v1/image/edit";

        if (incomingImageCount > 3)
            warnings.Add(new { type = "truncated", feature = "files", details = "Venice multi-edit supports up to 3 images. Extra images were dropped." });

        return "v1/image/multi-edit";
    }

    private static void EnsureRequiredInput(string endpoint, ImageRequest request, IReadOnlyList<ImageFile> files, JsonElement metadata)
    {
        if (endpoint == "v1/image/generate" || endpoint == "v1/image/edit" || endpoint == "v1/image/multi-edit")
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("Prompt is required.", nameof(request));
        }

        if (endpoint == "v1/image/upscale" || endpoint == "v1/image/background-remove")
        {
            var hasImageUrl = metadata.ValueKind == JsonValueKind.Object &&
                              metadata.TryGetProperty("image_url", out var imageUrlEl) &&
                              imageUrlEl.ValueKind == JsonValueKind.String &&
                              !string.IsNullOrWhiteSpace(imageUrlEl.GetString());

            if (files.Count == 0 && !hasImageUrl)
                throw new InvalidOperationException($"Venice endpoint '{endpoint}' requires an input image file or provider metadata image_url.");
        }
    }

    private static bool IsZImageTurboModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var normalized = model.Trim();
        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
            normalized = normalized[(slashIndex + 1)..];

        return string.Equals(normalized, "z-image-turbo", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseMultipart(IReadOnlyList<ImageFile> files, JsonElement metadata)
    {
        if (metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("content_type", out var contentTypeEl) &&
            contentTypeEl.ValueKind == JsonValueKind.String)
        {
            var forced = contentTypeEl.GetString();
            if (string.Equals(forced, "application/json", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(forced, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (files.Count == 0)
            return false;

        // URLs are JSON-only for these routes.
        if (files.Any(f => IsHttpUrl(f.Data)))
            return false;

        // Data URLs / base64 are ideal candidates for multipart upload.
        return true;
    }

    private static HttpContent BuildJsonContent(
        string endpoint,
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        JsonElement metadata,
        List<object> warnings)
    {
        var payload = CreatePayloadFromMetadata(metadata);

        if (endpoint == "v1/image/generate")
        {
            SetIfMissing(payload, "model", request.Model);
            SetIfMissing(payload, "prompt", request.Prompt);

            if (request.Seed is not null)
                SetIfMissing(payload, "seed", request.Seed.Value);

            if (request.N is not null)
            {
                var variants = Math.Clamp(request.N.Value, 1, 4);
                if (request.N.Value != variants)
                    warnings.Add(new { type = "clamped", feature = "n", details = "Venice variants are limited to 1..4. Value was clamped." });

                SetIfMissing(payload, "variants", variants);
            }

            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
                SetIfMissing(payload, "aspect_ratio", request.AspectRatio);

            if (TryParseSize(request.Size, out var width, out var height))
            {
                SetIfMissing(payload, "width", width);
                SetIfMissing(payload, "height", height);
            }
            else if (!string.IsNullOrWhiteSpace(request.Size))
            {
                warnings.Add(new { type = "unsupported", feature = "size", details = "Size format was not recognized. Use WIDTHxHEIGHT (e.g. 1024x1024)." });
            }

            if (files.Count > 0)
                warnings.Add(new { type = "ignored", feature = "files", details = "Generate route ignores files. Routing likely changed before send." });
        }
        else if (endpoint == "v1/image/upscale")
        {
            if (files.Count > 1)
                warnings.Add(new { type = "ignored", feature = "files", details = "Venice upscale accepts a single image. Extra images were ignored." });

            if (!payload.ContainsKey("image") && files.Count > 0)
                payload["image"] = NormalizeImageFieldForJson(files[0].Data);
        }
        else if (endpoint == "v1/image/background-remove")
        {
            if (files.Count > 1)
                warnings.Add(new { type = "ignored", feature = "files", details = "Venice background-remove accepts a single image. Extra images were ignored." });

            if (!payload.ContainsKey("image") && !payload.ContainsKey("image_url") && files.Count > 0)
                payload["image"] = NormalizeImageFieldForJson(files[0].Data);
        }
        else if (endpoint == "v1/image/edit")
        {
            SetIfMissing(payload, "prompt", request.Prompt);
            SetIfMissing(payload, "modelId", request.Model);
            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
                SetIfMissing(payload, "aspect_ratio", request.AspectRatio);

            var file = files.FirstOrDefault();
            if (file is null)
                throw new InvalidOperationException("Venice edit route requires 1 image.");

            SetIfMissing(payload, "image", NormalizeImageFieldForJson(file.Data));
        }
        else // v1/image/multi-edit
        {
            SetIfMissing(payload, "prompt", request.Prompt);
            SetIfMissing(payload, "modelId", request.Model);

            var selected = files.Take(3).Select(f => JsonValue.Create(NormalizeImageFieldForJson(f.Data))).ToArray();
            if (!payload.ContainsKey("images"))
                payload["images"] = new JsonArray(selected);
        }

        return new StringContent(payload.ToJsonString(JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json);
    }

    private static HttpContent BuildMultipartContent(
        string endpoint,
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        JsonElement metadata,
        List<object> warnings)
    {
        var form = new MultipartFormDataContent();

        if (endpoint == "v1/image/edit" || endpoint == "v1/image/multi-edit")
        {
            form.Add(new StringContent(request.Prompt), "prompt");
            form.Add(new StringContent(request.Model), "modelId");

            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
                form.Add(new StringContent(request.AspectRatio), "aspect_ratio");
        }

        // Raw passthrough for primitive metadata fields.
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (property.NameEquals("prompt") ||
                    property.NameEquals("model") ||
                    property.NameEquals("modelId") ||
                    property.NameEquals("image") ||
                    property.NameEquals("images") ||
                    property.NameEquals("image_url") ||
                    property.NameEquals("content_type"))
                    continue;

                if (ImagesTryToFormString(property.Value, out var value))
                    form.Add(new StringContent(value), property.Name);
            }
        }

        if (endpoint == "v1/image/upscale" || endpoint == "v1/image/background-remove")
        {
            var file = files.FirstOrDefault() ?? throw new InvalidOperationException($"Venice endpoint '{endpoint}' requires 1 image.");
            if (files.Count > 1)
                warnings.Add(new { type = "ignored", feature = "files", details = $"Venice route '{endpoint}' accepts a single image. Extra images were ignored." });

            form.Add(CreateFileContent(file), "image", "image-1" + GetImageExtension(file.MediaType));
        }
        else if (endpoint == "v1/image/edit")
        {
            var file = files.FirstOrDefault() ?? throw new InvalidOperationException("Venice edit route requires 1 image.");
            form.Add(CreateFileContent(file), "image", "image-1" + GetImageExtension(file.MediaType));
        }
        else
        {
            var selected = files.Take(3).ToList();
            if (files.Count > 3)
                warnings.Add(new { type = "truncated", feature = "files", details = "Venice multi-edit supports up to 3 images. Extra images were dropped." });

            for (var i = 0; i < selected.Count; i++)
            {
                var file = selected[i];
                form.Add(CreateFileContent(file), "images", $"image-{i + 1}{GetImageExtension(file.MediaType)}");
            }
        }

        return form;
    }

    private async Task<ImageResponse> ParseImageResponseAsync(
        HttpResponseMessage response,
        string endpoint,
        string model,
        List<object> warnings,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;

        if (!response.IsSuccessStatusCode)
        {
            var rawError = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Venice image request failed ({(int)response.StatusCode}) [{endpoint}]: {rawError}");
        }

        if (!string.IsNullOrWhiteSpace(mediaType) && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var dataUrl = Convert.ToBase64String(bytes).ToDataUrl(mediaType);

            var providerMeta = new JsonObject
            {
                ["endpoint"] = endpoint,
                ["status"] = (int)response.StatusCode,
                ["contentType"] = mediaType
            };

            return new ImageResponse
            {
                Images = [dataUrl],
                Warnings = warnings,
                ProviderMetadata = new Dictionary<string, JsonElement>
                {
                    [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMeta, JsonSerializerOptions.Web)
                },
                Response = new ResponseData
                {
                    Timestamp = now,
                    ModelId = model,
                    Body = new { endpoint, contentType = mediaType, binary = true }
                }
            };
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var images = new List<string>();
        if (root.TryGetProperty("images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var imageEl in imagesEl.EnumerateArray())
            {
                if (imageEl.ValueKind != JsonValueKind.String)
                    continue;

                var encoded = imageEl.GetString();
                if (string.IsNullOrWhiteSpace(encoded))
                    continue;

                if (encoded.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
                    images.Add(encoded);
                else
                    images.Add(encoded.ToDataUrl(ResolveOutputMediaType(root)));
            }
        }

        if (images.Count == 0)
            throw new InvalidOperationException($"Venice image response did not contain images for endpoint [{endpoint}].");

        var providerMetadata = new JsonObject
        {
            ["endpoint"] = endpoint,
            ["status"] = (int)response.StatusCode,
            ["body"] = JsonNode.Parse(root.GetRawText())
        };

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMetadata, JsonSerializerOptions.Web)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = model,
                Body = root.Clone()
            }
        };
    }

    private static string ResolveOutputMediaType(JsonElement root)
    {
        if (root.TryGetProperty("request", out var requestEl) &&
            requestEl.ValueKind == JsonValueKind.Object &&
            requestEl.TryGetProperty("format", out var formatEl) &&
            formatEl.ValueKind == JsonValueKind.String)
        {
            return formatEl.GetString()?.ToLowerInvariant() switch
            {
                "png" => MediaTypeNames.Image.Png,
                "jpeg" => MediaTypeNames.Image.Jpeg,
                "jpg" => MediaTypeNames.Image.Jpeg,
                "webp" => "image/webp",
                _ => "image/webp"
            };
        }

        return "image/webp";
    }

    private static JsonObject CreatePayloadFromMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return new JsonObject();

        var payload = JsonNode.Parse(metadata.GetRawText()) as JsonObject ?? new JsonObject();
        payload.Remove("content_type");
        return payload;
    }

    private static void SetIfMissing(JsonObject payload, string key, string? value)
    {
        if (!payload.ContainsKey(key) && !string.IsNullOrWhiteSpace(value))
            payload[key] = value;
    }

    private static void SetIfMissing(JsonObject payload, string key, int value)
    {
        if (!payload.ContainsKey(key))
            payload[key] = value;
    }

    private static string NormalizeImageFieldForJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        if (IsHttpUrl(value))
            return value;

        // Keep original form when already a data URL, otherwise send raw base64.
        if (MediaContentHelpers.TryParseDataUrl(value, out _, out var parsedBase64))
            return parsedBase64;

        return value;
    }

    private static ByteArrayContent CreateFileContent(ImageFile file)
    {
        var base64 = file.Data;
        if (MediaContentHelpers.TryParseDataUrl(file.Data, out _, out var parsedBase64))
            base64 = parsedBase64;

        var bytes = Convert.FromBase64String(base64);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(file.MediaType)
                ? MediaTypeNames.Application.Octet
                : file.MediaType);

        return content;
    }

    private static bool ImagesTryToFormString(JsonElement value, out string result)
    {
        result = string.Empty;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                result = value.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
                result = value.GetRawText();
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                result = value.GetBoolean() ? "true" : "false";
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseSize(string? value, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().Replace(':', 'x').ToLowerInvariant();
        var parts = normalized.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height))
            return false;

        return width > 0 && height > 0;
    }

    private static bool IsHttpUrl(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string GetImageExtension(string? mediaType)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Png => ".png",
            MediaTypeNames.Image.Jpeg => ".jpg",
            "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            _ => ".bin"
        };
    }
}
