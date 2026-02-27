using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.QuiverAI;

public partial class QuiverAIProvider
{
    private async Task<ImageResponse> ImageRequestCoreAsync(ImageRequest request, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var payload = BuildQuiverImagePayload(request);
        var route = ResolveQuiverImageRoute(request, payload);

        using var req = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(payload.ToJsonString(JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"QuiverAI image request failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var urls = new List<string>();
        var dataUrls = new List<string>();
        CollectImages(root, urls, dataUrls);

        foreach (var url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var mediaResp = await _client.GetAsync(url, cancellationToken);
            var bytes = await mediaResp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!mediaResp.IsSuccessStatusCode)
                continue;

            var mediaType = mediaResp.Content.Headers.ContentType?.MediaType
                ?? GuessImageMediaType(url)
                ?? MediaTypeNames.Image.Jpeg;

            dataUrls.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
        }

        var distinct = dataUrls
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (distinct.Count == 0)
            warnings.Add(new { type = "empty", feature = "images", details = "No image/svg payload found in provider response." });

        return new ImageResponse
        {
            Images = distinct,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private JsonObject BuildQuiverImagePayload(ImageRequest request)
    {
        var providerObj = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = providerObj.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(providerObj.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();

        if (!payload.ContainsKey("model") && !string.IsNullOrWhiteSpace(request.Model))
            payload["model"] = request.Model;

        if (!payload.ContainsKey("prompt") && !string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        if (!payload.ContainsKey("size") && !string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size;

        if (!payload.ContainsKey("aspectRatio") && !string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspectRatio"] = request.AspectRatio;

        if (!payload.ContainsKey("seed") && request.Seed is not null)
            payload["seed"] = request.Seed.Value;

        if (!payload.ContainsKey("n") && request.N is not null)
            payload["n"] = request.N.Value;

        if (!payload.ContainsKey("files") && request.Files is not null)
            payload["files"] = JsonSerializer.SerializeToNode(request.Files, JsonSerializerOptions.Web);

        if (!payload.ContainsKey("mask") && request.Mask is not null)
            payload["mask"] = JsonSerializer.SerializeToNode(request.Mask, JsonSerializerOptions.Web);

        return payload;
    }

    private static string ResolveQuiverImageRoute(ImageRequest request, JsonObject payload)
    {
        if (payload.TryGetPropertyValue("endpoint", out var endpointNode) && endpointNode is JsonValue ev)
        {
            var endpoint = ev.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                payload.Remove("endpoint");
                return endpoint.TrimStart('/');
            }
        }

        if (payload.TryGetPropertyValue("operation", out var opNode) && opNode is JsonValue ov)
        {
            var operation = ov.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(operation) && operation.Contains("vector", StringComparison.OrdinalIgnoreCase))
                return "v1/svgs/vectorizations";
        }

        if (request.Files?.Any() == true && string.IsNullOrWhiteSpace(request.Prompt))
            return "v1/svgs/vectorizations";

        return "v1/svgs/generations";
    }

    private static void CollectImages(JsonElement element, List<string> urls, List<string> dataUrls)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    CollectImages(prop.Value, urls, dataUrls);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectImages(item, urls, dataUrls);
                break;

            case JsonValueKind.String:
            {
                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    return;

                if (value.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("data:application/svg+xml", StringComparison.OrdinalIgnoreCase))
                {
                    dataUrls.Add(value);
                    return;
                }

                if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    urls.Add(value);
                    return;
                }

                if (value.Contains("<svg", StringComparison.OrdinalIgnoreCase))
                {
                    dataUrls.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).ToDataUrl("image/svg+xml"));
                }

                break;
            }
        }
    }

    private static string? GuessImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return "image/svg+xml";

        if (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Png;

        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";

        if (url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Gif;

        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Jpeg;

        return null;
    }
}

