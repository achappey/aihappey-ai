using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Blink;

public partial class BlinkProvider
{
    private async Task<ImageResponse> ImageRequestBlink(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var startedAt = DateTime.UtcNow;
        var warnings = BuildImageWarnings(request);

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "prompt", "model", "n", "images", "output_format"
        };

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["model"] = request.Model
        };

        if (request.N is > 0)
            payload["n"] = request.N.Value;

        if (request.ProviderOptions is not null)
        {
            if (request.ProviderOptions.TryGetValue(GetIdentifier(), out var providerOptions)
                && providerOptions.ValueKind == JsonValueKind.Object)
            {
                if (providerOptions.TryGetProperty("output_format", out var outputFormatEl)
                    && outputFormatEl.ValueKind == JsonValueKind.String)
                {
                    var format = outputFormatEl.GetString();
                    if (!string.IsNullOrWhiteSpace(format))
                        payload["output_format"] = format;
                }
            }
        }

        MergeRawProviderOptions(payload, request.ProviderOptions, GetIdentifier(), blocked);

        // reserve canonical mapping precedence
        payload["prompt"] = request.Prompt;
        payload["model"] = request.Model;
        if (request.N is > 0)
            payload["n"] = request.N.Value;

        var json = JsonSerializer.Serialize(payload, BlinkMediaJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/ai/image")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Blink API error: {(int)response.StatusCode} {response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var images = await ExtractImagesAsync(root, warnings, cancellationToken);

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new()
            {
                Timestamp = startedAt,
                ModelId = request.Model,
                Body = root.Clone()
            },
            Usage = ReadImageUsage(root)
        };
    }

    private static List<object> BuildImageWarnings(ImageRequest request)
    {
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Size))
            AddUnsupportedWarning(warnings, "size", "Blink image endpoint does not accept size directly; use providerOptions.blink properties when needed.");

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            AddUnsupportedWarning(warnings, "aspectRatio", "Blink image endpoint does not accept aspect_ratio directly.");

        if (request.Seed is not null)
            AddUnsupportedWarning(warnings, "seed");

        if (request.Mask is not null)
            AddUnsupportedWarning(warnings, "mask", "Mask uploads are not supported for Blink image endpoint.");

        if (request.Files is not null && request.Files.Any())
            AddUnsupportedWarning(warnings, "files", "Image file/url inputs are not supported for Blink image endpoint.");

        return warnings;
    }

    private async Task<List<string>> ExtractImagesAsync(JsonElement root, List<object> warnings, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return images;

        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;

            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            try
            {
                var downloaded = await TryFetchAsBase64Async(url, cancellationToken);
                if (downloaded is null)
                {
                    warnings.Add(new { type = "fetch_failed", url, details = "Unable to fetch Blink image output URL." });
                    continue;
                }

                images.Add(downloaded.Value.Base64.ToDataUrl(downloaded.Value.MediaType));
            }
            catch
            {
                warnings.Add(new { type = "fetch_failed", url, details = "Unable to fetch Blink image output URL." });
            }
        }

        return images;
    }

    private static ImageUsageData? ReadImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
            return null;

        if (usageEl.TryGetProperty("creditsCharged", out _))
        {
            return new ImageUsageData();
        }

        return null;
    }
}

