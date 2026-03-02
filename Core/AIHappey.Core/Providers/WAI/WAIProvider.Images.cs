using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.WAI;

public partial class WAIProvider
{
    private static readonly JsonSerializerOptions WAIImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> ImageRequestWAI(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var files = request.Files?.ToList() ?? [];

        HttpResponseMessage httpResponse;
        string endpoint;

        if (files.Count > 0)
        {
            endpoint = "v1/images/edits";
            httpResponse = await SendEditAsync(request, files, metadata, warnings, cancellationToken);
        }
        else
        {
            endpoint = "v1/images/generations";
            httpResponse = await SendGenerationAsync(request, metadata, warnings, cancellationToken);
        }

        var raw = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"WAI image request failed ({(int)httpResponse.StatusCode}) [{endpoint}]: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var images = await ParseImagesAsync(root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("WAI image response did not contain images.");

        var usage = ParseUsage(root);
        var timestamp = ResolveTimestamp(root, now);

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                endpoint,
                body = root.Clone()
            }, JsonSerializerOptions.Web)
        };

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = usage,
            ProviderMetadata = providerMetadata,
            Response = new ResponseData
            {
                Timestamp = timestamp,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private async Task<HttpResponseMessage> SendGenerationAsync(
        ImageRequest request,
        JsonElement metadata,
        List<object> warnings,
        CancellationToken cancellationToken)
    {
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", details = "WAI generation route does not accept mask without an input image." });

        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Files are only used on /v1/images/edits." });

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "WAI image route does not document n. A single image is requested." });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "WAI image route uses size. Aspect ratio is ignored unless mapped by caller." });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["size"] = request.Size,
            ["quality"] = GetString(metadata, "quality"),
            ["seed"] = request.Seed ?? GetInt(metadata, "seed"),
            ["steps"] = GetInt(metadata, "steps"),
            ["guidance_scale"] = GetDouble(metadata, "guidance_scale"),
            ["negative_prompt"] = GetString(metadata, "negative_prompt"),
            ["stream"] = GetBool(metadata, "stream")
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, WAIImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        return await _client.SendAsync(req, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendEditAsync(
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        JsonElement metadata,
        List<object> warnings,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(request.Model), "model");
        form.Add(new StringContent(request.Prompt), "prompt");

        AddOptionalString(form, "size", request.Size);
        AddOptionalString(form, "quality", GetString(metadata, "quality"));
        AddOptionalString(form, "negative_prompt", GetString(metadata, "negative_prompt"));

        var seed = request.Seed ?? GetInt(metadata, "seed");
        if (seed.HasValue)
            form.Add(new StringContent(seed.Value.ToString()), "seed");

        var steps = GetInt(metadata, "steps");
        if (steps.HasValue)
            form.Add(new StringContent(steps.Value.ToString()), "steps");

        var guidanceScale = GetDouble(metadata, "guidance_scale");
        if (guidanceScale.HasValue)
            form.Add(new StringContent(guidanceScale.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "guidance_scale");

        var stream = GetBool(metadata, "stream");
        if (stream.HasValue)
            form.Add(new StringContent(stream.Value ? "true" : "false"), "stream");

        for (var i = 0; i < files.Count; i++)
            form.Add(CreateImageContent(files[i]), "image", $"image-{i + 1}{GetImageExtension(files[i].MediaType)}");

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", details = "WAI edit docs do not expose mask; ignored." });

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "WAI edit docs do not expose n; response count is provider-defined." });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "WAI edit route uses size. Aspect ratio is ignored unless mapped by caller." });

        return await _client.PostAsync("v1/images/edits", form, cancellationToken);
    }

    private async Task<List<string>> ParseImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in dataEl.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var b64El) && b64El.ValueKind == JsonValueKind.String)
            {
                var b64 = b64El.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    images.Add(b64.ToDataUrl(MediaTypeNames.Image.Png));

                continue;
            }

            if (item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                var url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                using var imageResponse = await _client.GetAsync(url, cancellationToken);
                var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                if (!imageResponse.IsSuccessStatusCode || bytes.Length == 0)
                    continue;

                var mediaType = imageResponse.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    mediaType = MediaTypeNames.Image.Png;

                images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
            }
        }

        return images;
    }

    private static ImageUsageData? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
            return null;

        var inputTokens = GetInt(usageEl, "prompt_tokens") ?? GetInt(usageEl, "input_tokens");
        var outputTokens = GetInt(usageEl, "completion_tokens") ?? GetInt(usageEl, "output_tokens");
        var totalTokens = GetInt(usageEl, "total_tokens");

        if (inputTokens is null && outputTokens is null && totalTokens is null)
            return null;

        return new ImageUsageData
        {
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            TotalTokens = totalTokens
        };
    }

    private static DateTime ResolveTimestamp(JsonElement root, DateTime fallbackUtc)
    {
        if (root.TryGetProperty("created", out var createdEl) &&
            createdEl.ValueKind == JsonValueKind.Number &&
            createdEl.TryGetInt64(out var unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            }
            catch
            {
                return fallbackUtc;
            }
        }

        return fallbackUtc;
    }

    private static ByteArrayContent CreateImageContent(ImageFile file)
    {
        var bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix());
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(file.MediaType)
                ? MediaTypeNames.Application.Octet
                : file.MediaType);

        return content;
    }

    private static string GetImageExtension(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return ".bin";

        return mediaType.ToLowerInvariant() switch
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

    private static void AddOptionalString(MultipartFormDataContent form, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value), field);
    }

    private static string? GetString(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.TryGetProperty(propertyName, out var el) || el.ValueKind != JsonValueKind.String)
            return null;

        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? GetInt(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.TryGetProperty(propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            return n;

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out n))
            return n;

        return null;
    }

    private static double? GetDouble(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.TryGetProperty(propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n))
            return n;

        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), System.Globalization.CultureInfo.InvariantCulture, out n))
            return n;

        return null;
    }

    private static bool? GetBool(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.TryGetProperty(propertyName, out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
            _ => null
        };
    }
}
