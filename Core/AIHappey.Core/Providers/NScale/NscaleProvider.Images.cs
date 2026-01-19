using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Text.Json;
using AIHappey.Common.Extensions;
using System.Text;
using System.Text.Json.Serialization;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Nscale;

public partial class NscaleProvider : IModelProvider
{

    private static readonly JsonSerializerOptions imageSettings = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        // Step 2: Build JSON payload
        var jsonBody = JsonSerializer.Serialize(new
        {
            prompt = imageRequest.Prompt,
            model = imageRequest.Model,
            n = imageRequest.N,
            size = imageRequest.Size,
        }, imageSettings);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "v1/images/generations"
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
        {
            try
            {

                using var errorDoc = JsonDocument.Parse(jsonResponse);
                var message = errorDoc.RootElement
                    .GetProperty("error")
                    .GetProperty("message")
                    .GetString();

                throw new Exception(message ?? jsonResponse);
            }
            catch (JsonException)
            {
                // fallback als het geen geldige JSON is
                throw new Exception(jsonResponse);
            }
        }

        List<string> images = [];
        List<object> warnings = [];
        using var doc = JsonDocument.Parse(jsonResponse);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new Exception("No image data array returned from Nscale API.");

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
            throw new Exception("No valid image data returned from Nscale API.");

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
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