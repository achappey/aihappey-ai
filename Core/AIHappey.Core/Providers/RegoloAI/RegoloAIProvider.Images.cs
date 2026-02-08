using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RegoloAI;

public partial class RegoloAIProvider
{
    private static readonly JsonSerializerOptions ImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequestRegolo(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);

        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

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

        if (imageRequest.Seed is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = imageRequest.Model,
            ["prompt"] = imageRequest.Prompt,
            ["n"] = imageRequest.N,
            ["size"] = string.IsNullOrWhiteSpace(imageRequest.Size) ? null : imageRequest.Size
        };

        var json = JsonSerializer.Serialize(payload, ImageJsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception(string.IsNullOrWhiteSpace(raw) ? $"{(int)resp.StatusCode} {resp.ReasonPhrase}" : raw);

        using var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new Exception("No image data returned from Regolo API.");

        var body = doc.RootElement.Clone();
        List<string> images = [];

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64El) || b64El.ValueKind != JsonValueKind.String)
                continue;

            var b64 = b64El.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add($"data:{MediaTypeNames.Image.Png};base64,{b64}");
        }

        if (images.Count == 0)
            throw new Exception("No valid images returned from Regolo API.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = body
            }
        };
    }
}
