using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SiliconFlow;

public partial class SiliconFlowProvider
{
    private static readonly JsonSerializerOptions ImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (imageRequest.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files"
            });
        }

        if (!string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspect_ratio"
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = imageRequest.Model,
            ["prompt"] = imageRequest.Prompt,
        };

        if (!string.IsNullOrWhiteSpace(imageRequest.Size))
        {
            payload["image_size"] = imageRequest.Size;
        }

        if (imageRequest.Seed is not null)
        {
            payload["seed"] = imageRequest.Seed;
        }

        if (imageRequest.N is not null)
        {
            payload["batch_size"] = imageRequest.N;
        }

        var jsonBody = JsonSerializer.Serialize(payload, ImageJsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var jsonResponse = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {jsonResponse}");

        using var doc = JsonDocument.Parse(jsonResponse);
        if (!doc.RootElement.TryGetProperty("images", out var imagesArray)
            || imagesArray.ValueKind != JsonValueKind.Array)
        {
            throw new Exception("No images array returned from SiliconFlow API.");
        }

        var images = new List<string>();
        var downloadClient = _factory.CreateClient();
        foreach (var item in imagesArray.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlProp))
                continue;

            var url = urlProp.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            using var imageResp = await downloadClient.GetAsync(url, cancellationToken);
            if (!imageResp.IsSuccessStatusCode)
                throw new Exception($"Failed to download SiliconFlow image: {imageResp.StatusCode}");

            var bytes = await imageResp.Content.ReadAsByteArrayAsync(cancellationToken);
            var mediaType = imageResp.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Octet;
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
        }

        if (images.Count == 0)
            throw new Exception("No valid image URLs returned from SiliconFlow API.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = JsonDocument.Parse(jsonResponse).RootElement.Clone()
            }
        };
    }
}
