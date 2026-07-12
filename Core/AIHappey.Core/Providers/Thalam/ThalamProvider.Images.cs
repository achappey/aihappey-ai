using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Thalam;

public partial class ThalamProvider
{
    private async Task<ImageResponse> ThalamImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var providerOptions = GetThalamProviderOptions(request.ProviderOptions);
        var payload = BuildThalamImagePayload(request, providerOptions);
        var json = JsonSerializer.Serialize(payload, ThalamJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var httpResponse = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Thalam image generation failed ({(int)httpResponse.StatusCode})."
                : $"Thalam image generation failed ({(int)httpResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();

        var images = await ExtractThalamImagesAsync(root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("Thalam image response did not contain any downloadable images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractThalamImageUsage(root),
            ProviderMetadata = CreateThalamProviderMetadata(new
            {
                endpoint = "v1/images/generations",
                payload,
                response = root
            }),
            Response = new()
            {
                Timestamp = ResolveThalamTimestamp(root, now),
                Headers = httpResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> BuildThalamImagePayload(ImageRequest request, JsonElement providerOptions)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["size"] = request.Size,
            ["n"] = request.N,
            ["aspect_ratio"] = request.AspectRatio
        };

        var files = request.Files?.ToList() ?? [];
        if (files.Count == 1)
        {
            var file = files[0];
            payload["image"] = NormalizeThalamImageInput(file.Data, file.MediaType);
        }
        else if (files.Count > 1)
        {
            payload["image"] = files
                .Select(file => NormalizeThalamImageInput(file.Data, file.MediaType))
                .ToArray();
        }

        MergeThalamProviderOptions(payload, providerOptions);
        return payload;
    }

    private async Task<List<string>> ExtractThalamImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        List<string> images = [];

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in data.EnumerateArray())
        {
            var b64 = item.TryGetString("b64_json", "base64", "data");
            if (!string.IsNullOrWhiteSpace(b64))
            {
                images.Add(b64.ToDataUrl(GuessThalamMediaType(item.TryGetString("url")) ?? MediaTypeNames.Image.Png));
                continue;
            }

            var url = item.TryGetString("url", "image_url", "imageUrl");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var downloaded = await DownloadThalamMediaAsync(url, MediaTypeNames.Image.Png, cancellationToken);
            images.Add(Convert.ToBase64String(downloaded.Bytes).ToDataUrl(downloaded.MediaType));
        }

        return images;
    }

    private static ImageUsageData? ExtractThalamImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        return new ImageUsageData
        {
            InputTokens = usage.TryGetNumber("input_tokens", "inputTokens"),
            OutputTokens = usage.TryGetNumber("output_tokens", "outputTokens"),
            TotalTokens = usage.TryGetNumber("total_tokens", "totalTokens")
        };
    }

    private static DateTime ResolveThalamTimestamp(JsonElement root, DateTime fallback)
    {
        if (!root.TryGetProperty("created", out var created))
            return fallback;

        if (created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var unixSeconds))
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;

        return fallback;
    }
}
