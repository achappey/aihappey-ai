using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Monica;

public partial class MonicaProvider
{
    private static readonly JsonSerializerOptions MonicaImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var model = request.Model.Trim();
        var modelKey = model.ToLowerInvariant();
        var monicaOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var (endpoint, payload, isToolEndpoint) = modelKey switch
        {
            "flux_pro" or "flux_dev" or "flux_schnell"
                => ("v1/image/gen/flux", BuildGenerationPayload(request, monicaOptions, warnings, "num_outputs"), false),

            "sdxl" or "sd3" or "sd3_5"
                => ("v1/image/gen/sd", BuildGenerationPayload(request, monicaOptions, warnings, "num_outputs"), false),

            "dall-e-3"
                => ("v1/image/gen/dalle", BuildGenerationPayload(request, monicaOptions, warnings, "n"), false),

            "playground-v2-5"
                => ("v1/image/gen/playground", BuildGenerationPayload(request, monicaOptions, warnings, "count"), false),

            "v_2"
                => ("v1/image/gen/ideogram", BuildGenerationPayload(request, monicaOptions, warnings, null), false),

            "upscale"
                => ("v1/image/tool/upscale", BuildUpscalePayload(request, monicaOptions, warnings), true),

            "removebg"
                => ("v1/image/tool/removebg", BuildRemoveBackgroundPayload(request, monicaOptions, warnings), true),

            _ => throw new NotSupportedException($"Monica image model '{request.Model}' is not supported.")
        };

        var jsonBody = JsonSerializer.Serialize(payload, MonicaImageJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Monica image request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        var urls = isToolEndpoint
            ? ParseToolImageUrls(root)
            : ParseGenerationImageUrls(root);

        if (urls.Count == 0)
            throw new InvalidOperationException("Monica image response did not contain any image URLs.");

        var images = new List<string>(urls.Count);
        foreach (var url in urls)
            images.Add(await DownloadAsDataUrlAsync(url, cancellationToken));

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private Dictionary<string, object?> BuildGenerationPayload(
        ImageRequest request,
        JsonElement monicaOptions,
        List<object> warnings,
        string? countField)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required for Monica image generation models.", nameof(request));

        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Monica generation endpoints are text-to-image; ignored files." });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", details = "Monica generation endpoints do not support mask in this provider implementation." });

        if (countField is null && request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n", details = "This Monica model does not support n/count; ignored n." });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["size"] = string.IsNullOrWhiteSpace(request.Size) ? null : request.Size,
            ["seed"] = request.Seed
        };

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        if (countField is not null && request.N is not null)
            payload[countField] = request.N.Value;

        MergeMonicaProviderOptions(payload, monicaOptions, ReservedGenerationKeys(countField));
        return payload;
    }

    private Dictionary<string, object?> BuildUpscalePayload(
        ImageRequest request,
        JsonElement monicaOptions,
        List<object> warnings)
    {
        WarnUnsupportedToolFields(request, warnings, supportsN: false);

        var imageUrl = ResolveToolInputImageUrl(request, monicaOptions);
        var payload = new Dictionary<string, object?>
        {
            ["image"] = imageUrl,
            ["scale"] = ReadIntProperty(monicaOptions, "scale") ?? 2
        };

        MergeMonicaProviderOptions(payload, monicaOptions, ["image", "scale"]);
        return payload;
    }

    private Dictionary<string, object?> BuildRemoveBackgroundPayload(
        ImageRequest request,
        JsonElement monicaOptions,
        List<object> warnings)
    {
        WarnUnsupportedToolFields(request, warnings, supportsN: false);

        var payload = new Dictionary<string, object?>
        {
            ["image"] = ResolveToolInputImageUrl(request, monicaOptions)
        };

        MergeMonicaProviderOptions(payload, monicaOptions, ["image"]);
        return payload;
    }

    private static void WarnUnsupportedToolFields(ImageRequest request, List<object> warnings, bool supportsN)
    {
        if (!string.IsNullOrWhiteSpace(request.Prompt))
            warnings.Add(new { type = "unsupported", feature = "prompt", details = "Tool models use an input image URL; ignored prompt when image is provided via provider options." });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Monica tool endpoints require image URL input; ignored files." });
        if (!string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!supportsN && request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n" });
    }

    private static string ResolveToolInputImageUrl(ImageRequest request, JsonElement monicaOptions)
    {
        var fromOptions = ReadStringProperty(monicaOptions, "image");
        if (IsHttpUrl(fromOptions))
            return fromOptions!;

        if (IsHttpUrl(request.Prompt))
            return request.Prompt;

        var firstFile = request.Files?.FirstOrDefault();
        if (firstFile is not null && IsHttpUrl(firstFile.Data))
            return firstFile.Data;

        throw new ArgumentException(
            "Monica image tool models require an image URL. Provide providerOptions.monica.image (preferred) or prompt as an https URL.",
            nameof(request));
    }

    private static List<string> ParseGenerationImageUrls(JsonElement root)
    {
        var urls = new List<string>();

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return urls;

        foreach (var item in dataEl.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;

            var url = urlEl.GetString();
            if (IsHttpUrl(url))
                urls.Add(url!);
        }

        return urls;
    }

    private static List<string> ParseToolImageUrls(JsonElement root)
    {
        var urls = new List<string>();

        if (root.TryGetProperty("image", out var imageEl) && imageEl.ValueKind == JsonValueKind.String)
        {
            var url = imageEl.GetString();
            if (IsHttpUrl(url))
                urls.Add(url!);
        }

        return urls;
    }

    private async Task<string> DownloadAsDataUrlAsync(string url, CancellationToken cancellationToken)
    {
        using var fileResp = await _client.GetAsync(url, cancellationToken);
        var bytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Monica image download failed ({(int)fileResp.StatusCode}): {err}");
        }

        var mediaType = fileResp.Content.Headers.ContentType?.MediaType
            ?? GuessImageMediaType(url)
            ?? MediaTypeNames.Image.Png;

        return Convert.ToBase64String(bytes).ToDataUrl(mediaType);
    }

    private static string? GuessImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var value = url.Trim().ToLowerInvariant();
        if (value.Contains(".png")) return "image/png";
        if (value.Contains(".jpg") || value.Contains(".jpeg")) return "image/jpeg";
        if (value.Contains(".webp")) return "image/webp";
        if (value.Contains(".gif")) return "image/gif";
        if (value.Contains(".bmp")) return "image/bmp";
        if (value.Contains(".avif")) return "image/avif";

        return null;
    }

    private static bool IsHttpUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    private static string[] ReservedGenerationKeys(string? countField)
    {
        if (string.IsNullOrWhiteSpace(countField))
            return ["model", "prompt", "size", "seed", "aspect_ratio"];

        return ["model", "prompt", "size", "seed", "aspect_ratio", countField];
    }

    private static void MergeMonicaProviderOptions(
        Dictionary<string, object?> payload,
        JsonElement monicaOptions,
        IReadOnlyCollection<string> reservedKeys)
    {
        if (monicaOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in monicaOptions.EnumerateObject())
        {
            if (reservedKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static string? ReadStringProperty(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        if (!obj.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? ReadIntProperty(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        if (!obj.TryGetProperty(propertyName, out var value))
            return null;

        return value.TryGetInt32(out var parsed) ? parsed : null;
    }
}
