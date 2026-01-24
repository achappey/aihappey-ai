using AIHappey.Core.AI;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OVHcloud;

public partial class OVHcloudProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "OVHcloud images/generations does not support image inputs. Ignored files."
            });
        }

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (request.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        if (request.N is not null && request.N.Value != 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "OVHcloud image generations returns one image per request. Generated a single image." });


        var size = request.Size?.Replace(":", "x", StringComparison.OrdinalIgnoreCase);
        var hasSize = !string.IsNullOrWhiteSpace(size);

        if (!hasSize && !string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            var inferred = request.AspectRatio.InferSizeFromAspectRatio();
            if (inferred is not null)
            {
                size = $"{inferred.Value.width}x{inferred.Value.height}";
                hasSize = true;
            }
        }

        if (!hasSize)
            size = "1024x1024";

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["size"] = size,
            ["response_format"] = "b64_json"
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"OVHcloud API error: {resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var images = new List<string>();

        if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
            dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataEl.EnumerateArray())
            {
                if (item.TryGetProperty("b64_json", out var b64El) &&
                    b64El.ValueKind == JsonValueKind.String)
                {
                    var b64 = b64El.GetString();
                    if (!string.IsNullOrWhiteSpace(b64))
                        images.Add(b64.ToDataUrl(MediaTypeNames.Image.Png));
                }
            }
        }

        if (images.Count == 0)
            throw new Exception("OVHcloud image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = doc.RootElement.Clone()
            }
        };
    }
}
