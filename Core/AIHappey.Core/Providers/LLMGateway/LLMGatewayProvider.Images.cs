using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LLMGateway;

public partial class LLMGatewayProvider
{
    private static readonly JsonSerializerOptions ImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));
        if (imageRequest.N is < 1 or > 10)
            throw new ArgumentException("N must be between 1 and 10.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var files = imageRequest.Files?.ToList() ?? [];
        var isEdit = files.Count > 0;

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (imageRequest.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = imageRequest.Model,
            ["prompt"] = imageRequest.Prompt
        };

        MergeLLMGatewayImageOptions(payload, imageRequest, isEdit, warnings);
        if (imageRequest.N.HasValue)
            payload["n"] = imageRequest.N.Value;
        if (!string.IsNullOrWhiteSpace(imageRequest.Size))
            payload["size"] = imageRequest.Size;
        if (!string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
            payload["aspect_ratio"] = imageRequest.AspectRatio;

        // Gemini honors aspect ratios; OpenAI/Azure image models require literal WxH sizes.
        var model = imageRequest.Model.Split('/').Last();
        var supportsAspectRatio = model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
            || (isEdit && !model.StartsWith("gpt-image-", StringComparison.OrdinalIgnoreCase));
        if (payload.ContainsKey("aspect_ratio") && !supportsAspectRatio)
        {
            payload.Remove("aspect_ratio");
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "Use a literal size for this model." });
        }

        if (isEdit)
            payload["images"] = files.Select(ToLLMGatewayImageReference).ToList();
        else
            payload["response_format"] = "b64_json";

        ApplyAuthHeader();
        var jsonBody = JsonSerializer.Serialize(payload, ImageJsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, isEdit ? "v1/images/edits" : "v1/images/generations")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var jsonResponse = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLM Gateway image request failed ({(int)resp.StatusCode}): {jsonResponse}");

        using var doc = JsonDocument.Parse(jsonResponse);
        var root = doc.RootElement;
        var outputFormat = ReadLLMGatewayImageString(root, "output_format")
            ?? (payload.TryGetValue("output_format", out var format) ? format?.ToString() : null);
        var images = ExtractImages(root, outputFormat);

        if (images.Count == 0)
            warnings.Add(new { type = "other", feature = "images", details = "LLM Gateway returned no images; the request may have been safety-filtered." });

        var metadata = new Dictionary<string, JsonElement>();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name != "data")
                metadata[property.Name] = property.Value.Clone();
        }

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractUsage(root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(metadata),
            Response = new ()
            {
                Timestamp = root.TryGetProperty("created", out var created)
                    && created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : now,
                Headers = resp.GetHeaders(),
                ModelId = (ReadLLMGatewayImageString(root, "model") ?? imageRequest.Model).ToModelId(GetIdentifier())
            }
        };
    }

    private void MergeLLMGatewayImageOptions(Dictionary<string, object?> payload, ImageRequest request,
        bool isEdit, List<object> warnings)
    {
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) != true)
            return;
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("LLM Gateway image provider options must be an object.", nameof(request));

        foreach (var option in options.EnumerateObject())
        {
            if (option.Value.ValueKind == JsonValueKind.Null)
                continue;
            // Core inputs and routing cannot be replaced by provider options.
            if (option.Name is "model" or "prompt" or "images")
                continue;
            var supported = option.Name is "n" or "size" or "quality" or "moderation" or "service_tier" or "aspect_ratio"
                || (isEdit ? option.Name is "background" or "input_fidelity" or "output_format" or "output_compression"
                    : option.Name is "style" or "response_format");
            if (!supported)
            {
                warnings.Add(new { type = "unsupported", feature = $"providerOptions.{GetIdentifier()}.{option.Name}" });
                continue;
            }
            payload[option.Name] = option.Value.Clone();
        }
    }

    private static object ToLLMGatewayImageReference(ImageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Type is "file_id" or "fileId")
            throw new NotSupportedException("LLM Gateway image edits do not support file IDs. Supply an HTTPS URL or an inline image.");

        if (file is ImageFileUrl url)
        {
            if (url.Url?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
                return new { image_url = ValidateLLMGatewayImageDataUrl(url.Url) };
            if (!Uri.TryCreate(url.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("LLM Gateway image URLs must use HTTPS.", nameof(file));
            return new { image_url = url.Url };
        }

        if (file.Type != "file" || string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("An inline image requires non-empty file data.", nameof(file));
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return new { image_url = ValidateLLMGatewayImageDataUrl(file.Data) };
        if (string.IsNullOrWhiteSpace(file.MediaType) || !file.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || file.MediaType.Contains(';') || file.MediaType.Contains(','))
            throw new ArgumentException("An inline image requires an image MIME type.", nameof(file));
        ValidateLLMGatewayImageBase64(file.Data);
        return new { image_url = $"data:{file.MediaType};base64,{file.Data}" };
    }

    private static string ValidateLLMGatewayImageDataUrl(string value)
    {
        var comma = value.IndexOf(',');
        if (comma < 0 || !value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            || !value[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected a base64 image data URL.");
        ValidateLLMGatewayImageBase64(value[(comma + 1)..]);
        return value;
    }

    private static void ValidateLLMGatewayImageBase64(string data)
    {
        try
        {
            if (Convert.FromBase64String(data).Length == 0)
                throw new ArgumentException("Image data cannot be empty.");
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Image data must be valid base64.", ex);
        }
    }

    private static string? ReadLLMGatewayImageString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> ExtractImages(JsonElement root, string? outputFormat)
    {
        List<string> images = [];

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("LLM Gateway image response is missing the data array.");

        foreach (var item in data.EnumerateArray())
        {
            var base64 = ReadLLMGatewayImageString(item, "b64_json");
            if (string.IsNullOrWhiteSpace(base64))
                throw new InvalidOperationException("LLM Gateway image response contains an image without b64_json.");
            if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                images.Add(ValidateLLMGatewayImageDataUrl(base64));
            else
                images.Add($"data:{ResolveLLMGatewayImageMimeType(base64, outputFormat)};base64,{base64}");
        }

        return images;
    }

    private static string ResolveLLMGatewayImageMimeType(string base64, string? outputFormat)
    {
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return MediaTypeNames.Image.Png;
        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255)
            return MediaTypeNames.Image.Jpeg;
        if (bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP")
            return "image/webp";
        return outputFormat?.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };
    }

    private static ImageUsageData? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var usageData = new ImageUsageData();

        if (usage.TryGetProperty("input_tokens", out var inputTokensEl) && inputTokensEl.ValueKind == JsonValueKind.Number && inputTokensEl.TryGetInt32(out var inputTokens))
            usageData.InputTokens = inputTokens;

        if (usage.TryGetProperty("output_tokens", out var outputTokensEl) && outputTokensEl.ValueKind == JsonValueKind.Number && outputTokensEl.TryGetInt32(out var outputTokens))
            usageData.OutputTokens = outputTokens;

        if (usage.TryGetProperty("total_tokens", out var totalTokensEl) && totalTokensEl.ValueKind == JsonValueKind.Number && totalTokensEl.TryGetInt32(out var totalTokens))
            usageData.TotalTokens = totalTokens;

        if (!usageData.InputTokens.HasValue && !usageData.OutputTokens.HasValue && !usageData.TotalTokens.HasValue)
            return null;

        return usageData;
    }
}
