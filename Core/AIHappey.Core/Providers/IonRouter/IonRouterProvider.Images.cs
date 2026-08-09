using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.IonRouter;

public partial class IonRouterProvider
{
    private static readonly JsonSerializerOptions IonRouterImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "IonRouter documents one image per request." });

        var payload = BuildIonRouterImagePayload(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, IonRouterImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"IonRouter image generation failed ({(int)response.StatusCode})."
                : $"IonRouter image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var images = new List<string>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("b64_json", out var base64) && base64.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(base64.GetString()))
                    images.Add(base64.GetString()!.ToDataUrl(MediaTypeNames.Image.Png));
                else if (item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(url.GetString()))
                    images.Add(url.GetString()!);
            }
        }

        if (images.Count == 0)
            throw new InvalidOperationException("IonRouter image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private Dictionary<string, object?> BuildIonRouterImagePayload(ImageRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && options.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in options.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        var size = ParseIonRouterSize(request.Size);
        if (size is null && !string.IsNullOrWhiteSpace(request.AspectRatio))
            size = request.AspectRatio.InferSizeFromAspectRatio();
        if (size is not null)
        {
            payload["width"] = size.Value.width;
            payload["height"] = size.Value.height;
        }

        if (request.Seed is not null)
            payload["seed"] = request.Seed.Value;

        return payload;
    }

    private static (int width, int height)? ParseIonRouterSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size))
            return null;

        var parts = size.Replace(':', 'x').Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], out var width)
            && int.TryParse(parts[1], out var height)
            && width > 0 && height > 0
                ? (width, height)
                : null;
    }
}
