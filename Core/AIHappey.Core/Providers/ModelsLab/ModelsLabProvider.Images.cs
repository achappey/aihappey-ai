using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.ModelsLab;

public partial class ModelsLabProvider
{
    private static readonly JsonSerializerOptions ImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> ImageRequestTextToImage(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var apiKey = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"No {nameof(ModelsLab)} API key.");


        var payload = new Dictionary<string, object?>
        {
            ["key"] = apiKey,
            ["model_id"] = request.Model,
            ["prompt"] = request.Prompt
        };

        MergeRawProviderOptions(payload, request.ProviderOptions);

        // Guard reserved keys after passthrough merge to keep base contract deterministic.
        payload["key"] = apiKey;
        payload["model_id"] = request.Model;
        payload["prompt"] = request.Prompt;

        var json = JsonSerializer.Serialize(payload, ImageJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/v6/images/text2img")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"ModelsLab API error: {(int)resp.StatusCode} {resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (root.TryGetProperty("status", out var statusEl)
            && statusEl.ValueKind == JsonValueKind.String
            && string.Equals(statusEl.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            var message = root.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String
                ? messageEl.GetString()
                : "Unknown ModelsLab error.";

            throw new Exception($"ModelsLab image generation error: {message}");
        }

        var warnings = BuildWarnings(root);
        var images = await ExtractImagesAsync(root, warnings, cancellationToken);

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = root.Clone()
        };

        if (root.TryGetProperty("meta", out var metaEl))
            providerMetadata["meta"] = metaEl.Clone();

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static void MergeRawProviderOptions(Dictionary<string, object?> payload, Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null || providerOptions.Count == 0)
            return;

        if (providerOptions.TryGetValue(nameof(ModelsLab).ToLowerInvariant(), out var modelslabOptions)
            && modelslabOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in modelslabOptions.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        foreach (var option in providerOptions)
        {
            if (string.Equals(option.Key, nameof(ModelsLab).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                continue;

            payload[option.Key] = option.Value.Clone();
        }
    }

    private static List<object> BuildWarnings(JsonElement root)
    {
        var warnings = new List<object>();

        if (root.TryGetProperty("status", out var statusEl)
            && statusEl.ValueKind == JsonValueKind.String
            && string.Equals(statusEl.GetString(), "processing", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new
            {
                type = "processing",
                details = root.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String
                    ? messageEl.GetString()
                    : "Generation is still processing.",
                fetch_result = root.TryGetProperty("fetch_result", out var fetchEl) && fetchEl.ValueKind == JsonValueKind.String
                    ? fetchEl.GetString()
                    : null
            });
        }

        return warnings;
    }

    private async Task<List<string>> ExtractImagesAsync(JsonElement root, List<object> warnings, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("output", out var outputEl) || outputEl.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var output in outputEl.EnumerateArray())
        {
            if (output.ValueKind != JsonValueKind.String)
                continue;

            var value = output.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(value);
                continue;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                try
                {
                    using var imageResp = await _client.GetAsync(uri, cancellationToken);
                    if (!imageResp.IsSuccessStatusCode)
                        continue;

                    var bytes = await imageResp.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length == 0)
                        continue;

                    var mediaType = imageResp.Content.Headers.ContentType?.MediaType
                        ?? GuessImageMediaType(value)
                        ?? MediaTypeNames.Image.Png;

                    images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
                }
                catch
                {
                    warnings.Add(new
                    {
                        type = "fetch_failed",
                        url = value,
                        details = "Failed to fetch image URL from provider output."
                    });
                }

                continue;
            }

            // Loose fallback for raw base64 payloads.
            images.Add(value.ToDataUrl(MediaTypeNames.Image.Png));
        }

        return images;
    }

    private static string? GuessImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var withoutQuery = url.Split('?', '#')[0];
        if (withoutQuery.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Png;
        if (withoutQuery.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Jpeg;
        if (withoutQuery.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Jpeg;
        if (withoutQuery.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (withoutQuery.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Gif;
        if (withoutQuery.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) return "image/bmp";
        if (withoutQuery.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return "image/svg+xml";

        return null;
    }
}
