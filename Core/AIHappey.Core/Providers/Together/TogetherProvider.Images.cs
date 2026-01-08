using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Text.Json;
using AIHappey.Common.Extensions;
using System.Text;
using AIHappey.Common.Model.Providers.Together;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider : IModelProvider
{

    private static readonly JsonSerializerOptions imageSettings = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var metadata = imageRequest.GetImageProviderMetadata<TogetherImageProviderMetadata>(GetIdentifier());
        // Step 2: Build JSON payload
        var jsonBody = JsonSerializer.Serialize(new
        {
            prompt = imageRequest.Prompt,
            model = imageRequest.Model,
            n = imageRequest.N,
            steps = metadata?.Steps,
            height = imageRequest.GetImageHeight() ?? 1024,
            width = imageRequest.GetImageWidth() ?? 1024,
            guidance_scale = metadata?.GuidanceScale,
            negative_prompt = metadata?.NegativePrompt,
            disable_safety_checker = metadata?.DisableSafetyChecker,
            seed = imageRequest.Seed,
            response_format = "base64",
            output_format = "png"
        }, imageSettings);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.together.xyz/v1/images/generations"
        )
        {
            Content = new StringContent(
                jsonBody,
                Encoding.UTF8,
                "application/json"
            )
        };

        // Step 3: Send request
        using var resp = await _client.SendAsync(request, cancellationToken);
        var jsonResponse = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {jsonResponse}");
        List<string> images = [];
        List<object> warnings = [];
        using var doc = JsonDocument.Parse(jsonResponse);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new Exception("No image data array returned from Together API.");

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64Prop))
                continue;

            var b64 = b64Prop.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add(b64.ToDataUrl("image/png"));
        }

        if (images.Count == 0)
            throw new Exception("No valid image data returned from Together API.");

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

        return new()
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
