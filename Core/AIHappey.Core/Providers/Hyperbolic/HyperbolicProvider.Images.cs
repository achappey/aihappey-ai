using AIHappey.Core.AI;
using System.Text.Json;
using System.Text;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Hyperbolic;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Hyperbolic;

public partial class HyperbolicProvider 
{
    public async Task<ImageResponse> ImageRequest(
     ImageRequest imageRequest,
     CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = imageRequest.GetProviderMetadata<HyperbolicImageProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;

        // ---- size handling ----
        int width = 1024;
        int height = 1024;

        if (!string.IsNullOrWhiteSpace(imageRequest.Size))
        {
            // supports "1024x1024" or "1024:1024"
            var parts = imageRequest.Size
                .Replace(":", "x", StringComparison.OrdinalIgnoreCase)
                .Split('x', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 2 &&
                int.TryParse(parts[0], out var w) &&
                int.TryParse(parts[1], out var h))
            {
                width = w;
                height = h;
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["model_name"] = imageRequest.Model,
            ["prompt"] = imageRequest.Prompt,
            ["width"] = width,
            ["height"] = height,
            ["backend"] = "auto",
        };

        if (!string.IsNullOrEmpty(metadata?.NegativePrompt))
        {
            payload["negative_prompt"] = metadata?.NegativePrompt;
        }

        if (imageRequest.Seed is not null)
        {
            payload["seed"] = imageRequest?.Seed;
        }

        if (metadata?.Steps is not null)
        {
            payload["steps"] = metadata?.Steps;
        }

        if (metadata?.CfgScale is not null)
        {
            payload["cfg_scale"] = metadata?.CfgScale;
        }

        // img-to-img
        if (imageRequest.Files?.Any() == true)
        {
            var file = imageRequest.Files.First();

            payload["image"] = file.Data.ToDataUrl(file.MediaType);
        }

        var json = JsonSerializer.Serialize(payload, jsonOptions);

        using var resp = await _client.PostAsync(
            "v1/image/generation",
            new StringContent(json, Encoding.UTF8, "application/json"),
            cancellationToken);

        var text = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {text}");

        using var doc = JsonDocument.Parse(text);
        var images = new List<string>();

        if (doc.RootElement.TryGetProperty("images", out var arr))
        {
            foreach (var img in arr.EnumerateArray())
            {
                if (img.TryGetProperty("image", out var b64))
                {
                    images.Add(
                        b64.GetString()!.ToDataUrl("image/png")
                    );
                }
            }
        }

        if (images.Count == 0)
            throw new Exception("Hyperbolic returned no images.");

        return new ImageResponse
        {
            Images = images,
            Response = new()
            {
                ModelId = imageRequest.Model,
                Timestamp = now
            }
        };
    }

}
