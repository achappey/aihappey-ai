using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Verda;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Verda;

public partial class VerdaProvider
    : IModelProvider
{
    private static readonly JsonSerializerOptions Flux1Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string Flux1Endpoint = "flux-dev/predict";

    public async Task<ImageResponse> Flux1ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var providerMetadata = imageRequest.GetProviderMetadata<VerdaImageProviderMetadata>(GetIdentifier());
        var flux1 = providerMetadata?.Flux1;

        var inputImage = imageRequest.Files?.FirstOrDefault();
        if (imageRequest.Files?.Skip(1).Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Multiple input images are not supported; used files[0]."
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

        var normalizedSize = NormalizeFluxSize(imageRequest.Size, imageRequest.AspectRatio);
        var outputFormat = flux1?.OutputFormat ?? "jpeg";

        var input = new Flux1Input
        {
            Prompt = imageRequest.Prompt,
            Size = normalizedSize,
            Seed = imageRequest.Seed,
            NumImages = imageRequest.N,
            NumInferenceSteps = flux1?.NumInferenceSteps,
            GuidanceScale = flux1?.GuidanceScale,
            EnableSafetyChecker = flux1?.EnableSafetyChecker,
            OutputFormat = flux1?.OutputFormat,
            OutputQuality = flux1?.OutputQuality,
            EnableBase64Output = true,
            Image = inputImage is null ? null : ToDataUrl(inputImage)
        };

        var payload = new Flux1Request { Input = input };
        var json = JsonSerializer.Serialize(payload, Flux1Json);

        using var resp = await _client.PostAsync(
            Flux1Endpoint,
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception(ExtractVerdaError(raw) ?? $"{resp.StatusCode}: {raw}");

        var mimeType = MapOutputFormatToMimeType(outputFormat);
        var images = ExtractFluxImages(raw, mimeType);
        if (images.Count == 0)
            throw new Exception("Verda returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }

    private static string? NormalizeFluxSize(string? size, string? aspectRatio)
    {
        if (!string.IsNullOrWhiteSpace(size))
        {
            return size
                .Trim()
                .Replace("x", "*", StringComparison.OrdinalIgnoreCase)
                .Replace(":", "*", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(aspectRatio))
        {
            var inferred = aspectRatio.InferSizeFromAspectRatio();
            if (inferred is not null)
                return $"{inferred.Value.width}*{inferred.Value.height}";
        }

        return null;
    }

    private static string ToDataUrl(ImageFile file)
    {
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;

        return file.Data.ToDataUrl(file.MediaType);
    }

    private static string MapOutputFormatToMimeType(string? outputFormat)
        => outputFormat?.Trim().ToLowerInvariant() switch
        {
            "png" => MediaTypeNames.Image.Png,
            "webp" => "image/webp",
            "jpeg" => MediaTypeNames.Image.Jpeg,
            "jpg" => MediaTypeNames.Image.Jpeg,
            _ => MediaTypeNames.Image.Jpeg
        };

    private static List<string> ExtractFluxImages(string rawJson, string mimeType)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        List<string> images = [];

        if (root.TryGetProperty("output", out var output))
        {
            AddImages(output, images, mimeType);
        }
        else if (root.TryGetProperty("images", out var imagesEl))
        {
            AddImages(imagesEl, images, mimeType);
        }
        else if (root.TryGetProperty("image", out var imageEl))
        {
            AddImages(imageEl, images, mimeType);
        }
        else if (root.TryGetProperty("data", out var dataEl))
        {
            AddImages(dataEl, images, mimeType);
        }

        return images;
    }

    private static void AddImages(JsonElement element, List<string> images, string mimeType)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AddImages(item, images, mimeType);
                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("outputs", out var outputs))
                {
                    AddImages(outputs, images, mimeType);
                    break;
                }

                if (element.TryGetProperty("image", out var image))
                {
                    AddImages(image, images, mimeType);
                    break;
                }

                if (element.TryGetProperty("b64_json", out var b64Json))
                {
                    AddImages(b64Json, images, mimeType);
                    break;
                }

                if (element.TryGetProperty("data", out var data))
                {
                    AddImages(data, images, mimeType);
                    break;
                }

                break;

            case JsonValueKind.String:
                var b64 = element.GetString();
                if (string.IsNullOrWhiteSpace(b64))
                    break;

                if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    images.Add(b64);
                    break;
                }

                images.Add(b64.ToDataUrl(mimeType));
                break;
        }
    }

    private static string? ExtractVerdaError(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();

                if (error.TryGetProperty("message", out var message))
                    return message.GetString();
            }

            if (root.TryGetProperty("detail", out var detail))
                return detail.ToString();
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }


    private sealed class Flux1Request
    {
        [JsonPropertyName("input")]
        public Flux1Input Input { get; set; } = null!;
    }

    private sealed class Flux1Input
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = null!;

        [JsonPropertyName("size")]
        public string? Size { get; set; }

        [JsonPropertyName("num_inference_steps")]
        public int? NumInferenceSteps { get; set; }

        [JsonPropertyName("seed")]
        public int? Seed { get; set; }

        [JsonPropertyName("guidance_scale")]
        public float? GuidanceScale { get; set; }

        [JsonPropertyName("num_images")]
        public int? NumImages { get; set; }

        [JsonPropertyName("enable_safety_checker")]
        public bool? EnableSafetyChecker { get; set; }

        [JsonPropertyName("output_format")]
        public string? OutputFormat { get; set; }

        [JsonPropertyName("output_quality")]
        public int? OutputQuality { get; set; }

        [JsonPropertyName("enable_base64_output")]
        public bool EnableBase64Output { get; set; }

        [JsonPropertyName("image")]
        public string? Image { get; set; }
    }
}

