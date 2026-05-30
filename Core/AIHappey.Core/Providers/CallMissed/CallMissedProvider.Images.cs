using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.CallMissed;

public partial class CallMissedProvider
{
    private static readonly JsonSerializerOptions CallMissedImageJsonOptions = new(JsonSerializerDefaults.Web)
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

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var requestedN = request.N ?? 1;
        if (requestedN < 1)
            requestedN = 1;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = requestedN,
            ["size"] = request.Size,
            ["response_format"] = "b64_json"
        };

        MergeProviderOptions(payload, request.GetProviderMetadata<JsonElement>(GetIdentifier()));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, CallMissedImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"CallMissed image generation failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("CallMissed did not return image data.");

        List<string> images = [];

        foreach (var item in dataEl.EnumerateArray())
        {
            var b64 = ReadStringProperty(item, "b64_json");
            if (!string.IsNullOrWhiteSpace(b64))
            {
                images.Add(b64!.ToDataUrl(MediaTypeNames.Image.Png));
                continue;
            }

            var url = ReadStringProperty(item, "url");
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var imageUri))
            {
                using var imageResp = await _client.GetAsync(imageUri, cancellationToken);
                if (!imageResp.IsSuccessStatusCode)
                    continue;

                var imageBytes = await imageResp.Content.ReadAsByteArrayAsync(cancellationToken);
                var mediaType = imageResp.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Image.Png;
                images.Add(Convert.ToBase64String(imageBytes).ToDataUrl(mediaType));
            }
        }

        if (images.Count == 0)
            throw new InvalidOperationException("CallMissed did not return any usable images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    endpoint = "v1/images/generations",
                    responseCount = images.Count
                })
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = raw
            }
        };
    }
}
