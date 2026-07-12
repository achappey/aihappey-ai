using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vivgrid;

public partial class VivgridProvider
{
    private async Task<ImageResponse> ImageRequestVivgrid(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = request.N,
            ["size"] = request.Size
        };

        MergeVivgridProviderOptions(payload, request.ProviderOptions, GetIdentifier());

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, VivgridJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vivgrid image generation failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var images = await ExtractVivgridImagesAsync(root, payload, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("Vivgrid image generation response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = BuildVivgridProviderMetadata(root),
            Response = new()
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = root.TryGetString("model")?.ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<List<string>> ExtractVivgridImagesAsync(
        JsonElement root,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();
        if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            return result;

        var outputFormat = TryGetPayloadString(payload, "output_format", "response_format");

        foreach (var item in dataElement.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var base64Element) && base64Element.ValueKind == JsonValueKind.String)
            {
                var base64 = base64Element.GetString();
                if (!string.IsNullOrWhiteSpace(base64))
                    result.Add(base64.ToDataUrl(ResolveVivgridImageMimeType(outputFormat)));
            }

            if (item.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String)
            {
                var url = urlElement.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var fetched = await TryFetchVivgridMediaAsBase64Async(url, cancellationToken);
                if (fetched is { } media)
                    result.Add(media.Base64.ToDataUrl(media.MediaType));
            }
        }

        return result;
    }
}
