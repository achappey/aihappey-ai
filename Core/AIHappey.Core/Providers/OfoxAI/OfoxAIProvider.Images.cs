using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.OfoxAI;

public partial class OfoxAIProvider
{
    private static readonly JsonSerializerOptions OfoxImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> ImageRequestOfoxAI(ImageRequest request, CancellationToken cancellationToken = default)
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
        var payload = BuildImagePayload(request, metadata, warnings);

        using var response = await _client.PostAsync(
            "v1/images/generations",
            new StringContent(payload.ToJsonString(OfoxImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OfoxAI image request failed ({(int)response.StatusCode}): {text}");

        var root = JsonNode.Parse(text) ?? throw new InvalidOperationException("OfoxAI image response was empty.");
        var images = await ExtractImagesAsync(root, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("OfoxAI image response did not contain images.");

        var body = JsonSerializer.Deserialize<object>(root.ToJsonString(), OfoxImageJsonOptions);
        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                endpoint = "v1/images/generations",
                body
            }, OfoxImageJsonOptions)
        };

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = ResolveTimestamp(root, now),
                ModelId = request.Model,
                Body = body
            }
        };
    }

    private static JsonObject BuildImagePayload(ImageRequest request, JsonElement metadata, List<object> warnings)
    {
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var payload = metadata.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(metadata.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        if (request.N.HasValue)
            payload["n"] = request.N.Value;

        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size;

        return payload;
    }

    private async Task<List<string>> ExtractImagesAsync(JsonNode root, CancellationToken cancellationToken)
    {
        var images = new List<string>();
        var data = root["data"]?.AsArray();

        if (data is null)
            return images;

        foreach (var item in data)
        {
            if (item?["b64_json"] is JsonNode b64)
            {
                images.Add(b64.GetValue<string>().ToDataUrl(MediaTypeNames.Image.Png));
                continue;
            }

            if (item?["url"] is not JsonNode urlNode)
                continue;

            var url = urlNode.GetValue<string>();
            var bytes = await _client.GetByteArrayAsync(url, cancellationToken);
            var mediaType = GuessImageMediaType(url);
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
        }

        return images;
    }

    private static DateTime ResolveTimestamp(JsonNode root, DateTime fallback)
    {
        var created = root["created"]?.GetValue<long?>();
        return created.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(created.Value).UtcDateTime
            : fallback;
    }

    private static string GuessImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return MediaTypeNames.Image.Png;

        var path = url.Split('?', '#')[0];

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".gif" => MediaTypeNames.Image.Gif,
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => MediaTypeNames.Image.Png
        };
    }
}
