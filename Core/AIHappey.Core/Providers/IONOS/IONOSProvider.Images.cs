using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.IONOS;

public partial class IONOSProvider
{
    private static readonly HashSet<string> SupportedSizes =
    [
        "1024*1024",
        "1792*1024",
        "1024*1792"
    ];

    private const string DefaultSize = "1024*1024";

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
                details = "IONOS images/generations does not support image inputs. Ignored files."
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
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "IONOS image generations currently handled as a single-image response. Generated a single image."
            });
        }

        string size;

        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            size = request.Size.Replace("x", "*", StringComparison.OrdinalIgnoreCase);
        }
        else if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            var inferred = request.AspectRatio.InferSizeFromAspectRatio();
            size = inferred is null
                ? DefaultSize
                : $"{inferred.Value.width}*{inferred.Value.height}";

            if (inferred is null)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "aspectRatio",
                    details = $"Requested aspect ratio {request.AspectRatio} could not be inferred. Used default size {DefaultSize}."
                });
            }
        }
        else
        {
            size = DefaultSize;
        }

        if (!SupportedSizes.Contains(size))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "size",
                details = $"Requested size {size} not supported by IONOS. Used default size {DefaultSize}."
            });

            size = DefaultSize;
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = 1,
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

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"IONOS API error: {response.StatusCode}: {raw}");

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
            throw new Exception("IONOS image generation returned no images.");

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
