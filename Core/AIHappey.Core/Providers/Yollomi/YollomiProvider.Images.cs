using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Yollomi;

public partial class YollomiProvider
{
    private async Task<ImageResponse> YollomiImageRequestAsync(
        ImageRequest request,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var payload = YollomiProviderOptions(request.ProviderOptions);
        payload["type"] = "image";
        payload["modelId"] = request.Model.SplitModelId().Model;
        if (!string.IsNullOrWhiteSpace(request.Prompt)) payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspectRatio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        if (request.N is not null) payload["numOutputs"] = request.N;
        if (request.Seed is not null) payload["seed"] = request.Seed;

        var files = request.Files?.Where(x => !string.IsNullOrWhiteSpace(x.Data)).ToArray() ?? [];
        if (files.Length > 0)
        {
            var values = files.Select(YollomiImageValue).ToArray();
            payload["imageUrl"] = values[0];
            payload["image"] = values[0];
            if (values.Length > 1) payload["images"] = values;
        }
        if (request.Mask is { Data.Length: > 0 }) payload["mask"] = YollomiImageValue(request.Mask);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Yollomi image request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        List<string> images = [];
        if (root.TryGetProperty("images", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value)) images.Add(await YollomiNormalizeImageAsync(value, cancellationToken));
            }
        }
        if (images.Count == 0 && root.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.String)
        {
            var value = image.GetString();
            if (!string.IsNullOrWhiteSpace(value)) images.Add(await YollomiNormalizeImageAsync(value, cancellationToken));
        }
        if (images.Count == 0)
            throw new InvalidOperationException("Yollomi image response contained no images.");

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders()
            }
        };
    }

    private static Dictionary<string, object?> YollomiProviderOptions(Dictionary<string, JsonElement>? options)
    {
        if (options?.TryGetValue("yollomi", out var metadata) != true || metadata.ValueKind != JsonValueKind.Object)
            return [];
        return metadata.EnumerateObject().ToDictionary(x => x.Name, x => (object?)x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    private static string YollomiImageValue(ImageFile image)
        => image.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || image.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? image.Data
                : image.Data.ToDataUrl(string.IsNullOrWhiteSpace(image.MediaType) ? MediaTypeNames.Image.Png : image.MediaType);

    private async Task<string> YollomiNormalizeImageAsync(string value, CancellationToken cancellationToken)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return value;
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return value.ToDataUrl(MediaTypeNames.Image.Png);

        using var response = await _client.GetAsync(value, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Yollomi image download failed ({(int)response.StatusCode}).");
        return Convert.ToBase64String(bytes).ToDataUrl(response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Image.Png);
    }
}
