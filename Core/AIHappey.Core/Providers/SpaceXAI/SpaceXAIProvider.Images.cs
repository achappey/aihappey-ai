using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SpaceXAI;

public partial class SpaceXAIProvider
{

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var now = DateTime.UtcNow;

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var inputImages = imageRequest.Files?.ToList() ?? [];
        if (inputImages.Count > 3)
            throw new ArgumentException("SpaceXAI supports at most three source images per image edit.", nameof(imageRequest));

        object payload = new
        {
            model = imageRequest.Model,
            prompt = imageRequest.Prompt,
            n = imageRequest.N,
            response_format = "b64_json"
        };

        if (inputImages.Count > 0)
        {
            var imageItems = inputImages.Select(ToSpaceXAIImageReference).ToList();

            payload = new
            {
                model = imageRequest.Model,
                prompt = imageRequest.Prompt,
                image = imageItems[0],
                images = imageItems.Count > 1 ? imageItems : null,
                aspect_ratio = imageRequest.AspectRatio,
                n = imageRequest.N,
                response_format = "b64_json"
            };
        }

        List<object> warnings = [];
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var endpoint = inputImages.Count == 0
            ? "v1/images/generations"
            : "v1/images/edits";
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // 3) Send request
        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception(string.IsNullOrWhiteSpace(raw) ? resp.ReasonPhrase : raw);

        // 4) Parse response
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new Exception("No image data returned");

        List<ResourceLinkBlock> resourceLinks = [];
        List<string> images = [];
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64El) || b64El.ValueKind != JsonValueKind.String)
                continue;

            var bytes = Convert.FromBase64String(b64El.GetString()!);

            images.Add(b64El.GetString()!.ToDataUrl("image/png"));
        }

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (imageRequest.Size is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aize"
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

    private static Dictionary<string, string> ToSpaceXAIImageReference(ImageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (string.Equals(file.Type, "url", StringComparison.OrdinalIgnoreCase))
        {
            return new(StringComparer.Ordinal)
            {
                ["url"] = file.Data,
                ["type"] = "image_url"
            };
        }

        if (string.Equals(file.Type, "file_id", StringComparison.OrdinalIgnoreCase))
        {
            return new(StringComparer.Ordinal)
            {
                ["file_id"] = file.Data,
                ["type"] = "file_id"
            };
        }

        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return new(StringComparer.Ordinal)
            {
                ["url"] = file.Data,
                ["type"] = "image_url"
            };
        }

        return new(StringComparer.Ordinal)
        {
            ["url"] = $"data:{file.MediaType};base64,{file.Data}",
            ["type"] = "image_url"
        };
    }

}
