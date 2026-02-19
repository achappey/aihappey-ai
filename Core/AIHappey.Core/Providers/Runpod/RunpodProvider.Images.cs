using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runpod;

public partial class RunpodProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var model = NormalizeRunpodModelId(request.Model);
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt)
            && request.Files?.Any() != true
            && request.Mask is null)
        {
            throw new ArgumentException("At least one image input is required (prompt, files, or mask).", nameof(request));
        }

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var input = BuildRunpodImageInput(request);
        var passthroughInput = TryGetRunpodPassthroughInput(request);
        MergeJsonObject(input, passthroughInput);

        if (input.Count == 0)
            throw new ArgumentException("Image input is required (prompt, files, mask, or providerOptions.runpod.input).", nameof(request));

        var payload = new JsonObject
        {
            ["input"] = input
        };

        var route = $"{model}/runsync";
        var payloadJson = payload.ToJsonString(JsonSerializerOptions.Web);

        using var submitResp = await _client.PostAsync(
            route,
            new StringContent(payloadJson, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken).ConfigureAwait(false);

        var submitRaw = await submitResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!submitResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runpod image request failed ({(int)submitResp.StatusCode}): {submitRaw}");

        using var submitDoc = JsonDocument.Parse(submitRaw);
        var root = submitDoc.RootElement;

        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;

        if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "TIMED_OUT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Runpod image generation failed with status '{status}': {root.GetRawText()}");
        }

        var imageUrls = ExtractImageUrls(root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (imageUrls.Count == 0)
            throw new InvalidOperationException("Runpod image response did not contain any image URLs.");

        List<string> images = [];
        foreach (var imageUrl in imageUrls)
        {
            if (imageUrl.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(imageUrl);
                continue;
            }

            using var mediaResp = await _client.GetAsync(imageUrl, cancellationToken).ConfigureAwait(false);
            var bytes = await mediaResp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!mediaResp.IsSuccessStatusCode)
            {
                var text = Encoding.UTF8.GetString(bytes);
                throw new InvalidOperationException($"Runpod image download failed ({(int)mediaResp.StatusCode}): {text}");
            }

            var mediaType = mediaResp.Content.Headers.ContentType?.MediaType
                ?? GuessImageMediaType(imageUrl)
                ?? MediaTypeNames.Image.Png;

            images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
        }

        var runpodMetadata = new Dictionary<string, JsonElement>
        {
            ["submit"] = root.Clone(),
            ["resolved_input"] = JsonSerializer.SerializeToElement(input, JsonSerializerOptions.Web)
        };

        if (passthroughInput is not null)
            runpodMetadata["passthrough_input"] = JsonSerializer.SerializeToElement(passthroughInput, JsonSerializerOptions.Web);

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(runpodMetadata, JsonSerializerOptions.Web)
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = model,
                Body = root.Clone()
            }
        };
    }

    private static JsonObject BuildRunpodImageInput(ImageRequest request)
    {
        var input = new JsonObject();

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            input["prompt"] = request.Prompt;

        if (request.Seed is not null)
            input["seed"] = request.Seed.Value;

        var size = NormalizeRunpodImageSize(request.Size);
        if (!string.IsNullOrWhiteSpace(size))
            input["size"] = size;

        if (request.Files?.Any() == true)
        {
            var files = request.Files.Select(ToRunpodImageInput).Where(static v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (files.Count == 1)
                input["image"] = files[0];
            else if (files.Count > 1)
                input["images"] = JsonSerializer.SerializeToNode(files, JsonSerializerOptions.Web);
        }

        if (request.Mask is not null)
            input["mask"] = ToRunpodImageInput(request.Mask);

        return input;
    }

    private static JsonObject? TryGetRunpodPassthroughInput(ImageRequest request)
    {
        if (request.ProviderOptions is null)
            return null;

        if (!request.ProviderOptions.TryGetValue("runpod", out var runpod))
            return null;

        if (runpod.ValueKind != JsonValueKind.Object)
            return null;

        if (!runpod.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return null;

        return JsonNode.Parse(input.GetRawText()) as JsonObject;
    }

    private static string? NormalizeRunpodImageSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return normalized.Replace('x', '*').Replace('X', '*').Replace(':', '*');
    }

    private static string ToRunpodImageInput(ImageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Image data is required.", nameof(file));

        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;

        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        if (!string.IsNullOrWhiteSpace(file.MediaType))
            return $"data:{file.MediaType};base64,{file.Data}";

        return file.Data;
    }

    private static List<string> ExtractImageUrls(JsonElement root)
    {
        List<string> urls = [];
        CollectImageUrls(root, urls);
        return urls;
    }

    private static void CollectImageUrls(JsonElement element, List<string> urls)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (property.Name.Contains("b64", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(value)
                            && IsLikelyBase64(value))
                        {
                            urls.Add(value.ToDataUrl(MediaTypeNames.Image.Png));
                        }
                        else if (IsImageUrlProperty(property.Name)
                            && TryGetUrl(value, out var imageUrl)
                            && IsImageUrlCandidate(property.Name, imageUrl))
                        {
                            urls.Add(imageUrl);
                        }
                    }

                    CollectImageUrls(property.Value, urls);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectImageUrls(item, urls);
                break;

            case JsonValueKind.String:
                var candidate = element.GetString();
                if (TryGetUrl(candidate, out var url) && LooksLikeImageUrl(url))
                    urls.Add(url);
                break;
        }
    }

    private static bool IsImageUrlProperty(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Equals("image_url", StringComparison.OrdinalIgnoreCase)
            || name.Equals("imageUrl", StringComparison.OrdinalIgnoreCase)
            || name.Equals("image", StringComparison.OrdinalIgnoreCase)
            || name.Equals("url", StringComparison.OrdinalIgnoreCase)
            || name.Equals("uri", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageUrlCandidate(string propertyName, string url)
    {
        if (propertyName.Equals("image_url", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("imageUrl", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("image", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return LooksLikeImageUrl(url);
    }

    private static bool LooksLikeImageUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return url.Contains("image", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=' || char.IsWhiteSpace(c))
                continue;

            return false;
        }

        return true;
    }

    private static string? GuessImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";

        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return "image/jpeg";

        if (url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            return "image/gif";

        if (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return "image/png";

        return null;
    }

}
