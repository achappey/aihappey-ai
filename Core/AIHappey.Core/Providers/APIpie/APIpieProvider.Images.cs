using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.APIpie;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.APIpie;

public partial class APIpieProvider
{
    private static readonly JsonSerializerOptions imageSettings = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = imageRequest.GetProviderMetadata<APIpieImageProviderMetadata>(GetIdentifier());

        var requestedN = imageRequest.N ?? 1;
        if (requestedN < 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = $"Requested n={requestedN} is invalid. Using n=1."
            });
            requestedN = 1;
        }

        if (requestedN > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = $"APIpie currently supports n=1. Requested n={requestedN}, using n=1."
            });
            requestedN = 1;
        }

        if (imageRequest.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "APIpie image generation expects image URL input via providerOptions.apipie.image when editing."
            });
        }

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        var payload = new
        {
            prompt = imageRequest.Prompt,
            model = imageRequest.Model,
            n = requestedN,
            size = imageRequest.Size,
            quality = metadata?.Quality,
            response_format = "b64_json",
            style = metadata?.Style,
            image = metadata?.Image,
            seed = imageRequest.Seed ?? metadata?.Seed,
            steps = metadata?.Steps,
            loras = metadata?.Loras,
            strength = metadata?.Strength,
            aspect_ratio = imageRequest.AspectRatio ?? metadata?.AspectRatio,
            text_layout = metadata?.TextLayout
        };

        var jsonBody = JsonSerializer.Serialize(payload, imageSettings);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            throw new Exception("No image data array returned from APIpie API.");

        List<string> images = [];

        foreach (var item in dataEl.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64El) || b64El.ValueKind != JsonValueKind.String)
                continue;

            var b64 = b64El.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add(b64.ToDataUrl("image/png"));
        }

        if (images.Count == 0)
            throw new Exception("No valid image data returned from APIpie API.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model
            }
        };
    }
}

