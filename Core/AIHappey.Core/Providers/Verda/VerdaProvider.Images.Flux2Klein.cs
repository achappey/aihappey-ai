using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Verda;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Verda;

public partial class VerdaProvider
    : IModelProvider
{
    private const string Flux2Klein4bEndpoint = "https://inference.datacrunch.io/flux2-klein-4b/generate";
    private const string Flux2KleinBase4bEndpoint = "https://inference.datacrunch.io/flux2-klein-base-4b/generate";
    private const string Flux2KleinBase9bEndpoint = "https://inference.datacrunch.io/flux2-klein-base-9b/generate";

    public async Task<ImageResponse> Flux2KleinImageRequest(
        ImageRequest imageRequest,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var providerMetadata = imageRequest.GetImageProviderMetadata<VerdaImageProviderMetadata>(GetIdentifier());
        var flux2Klein = providerMetadata?.Flux2Klein;

        if (imageRequest.N is > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "Flux.2 [klein] returns one image per request; generated a single image."
            });
        }

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask",
                details = "Flux.2 [klein] does not accept a separate mask input; value was ignored."
            });
        }

        var inputImages = imageRequest.Files?.Select(ToDataUrl).ToList();
        if (inputImages?.Count > 4)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Flux.2 [klein] supports up to 4 input images; extra images were ignored."
            });
            inputImages = inputImages.Take(4).ToList();
        }

        if (inputImages?.Count == 0)
            inputImages = null;

        var (width, height) = ResolveFlux2KleinWidthHeight(imageRequest, warnings);

        var outputFormat = flux2Klein?.OutputFormat ?? "jpeg";
        var isFixedGuidance = imageRequest.Model.Equals("flux2-klein-4b", StringComparison.OrdinalIgnoreCase);
        var numSteps = flux2Klein?.NumSteps;
        var guidance = flux2Klein?.Guidance;

        if (isFixedGuidance)
        {
            if (numSteps is not null)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "num_steps",
                    details = "Flux.2 [klein] 4B uses a fixed step count; provided value was ignored."
                });
            }

            if (guidance is not null)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "guidance",
                    details = "Flux.2 [klein] 4B uses fixed guidance; provided value was ignored."
                });
            }

            numSteps = null;
            guidance = null;
        }

        var endpoint = imageRequest.Model switch
        {
            "flux2-klein-4b" => Flux2Klein4bEndpoint,
            "flux2-klein-base-4b" => Flux2KleinBase4bEndpoint,
            "flux2-klein-base-9b" => Flux2KleinBase9bEndpoint,
            _ => throw new NotSupportedException($"Verda image model '{imageRequest.Model}' is not supported.")
        };

        var payload = new Flux2KleinRequest
        {
            Prompt = imageRequest.Prompt,
            Width = width,
            Height = height,
            NumSteps = numSteps,
            Guidance = guidance,
            Seed = imageRequest.Seed,
            InputImages = inputImages,
            EnableSafetyChecker = flux2Klein?.EnableSafetyChecker,
            OutputFormat = flux2Klein?.OutputFormat,
            OutputQuality = flux2Klein?.OutputQuality,
            EnableBase64Output = true
        };

        var json = JsonSerializer.Serialize(payload, Flux1Json);

        using var resp = await _client.PostAsync(
            endpoint,
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

    private static (int? width, int? height) ResolveFlux2KleinWidthHeight(
        ImageRequest request,
        List<object> warnings)
    {
        var size = request.Size
            ?.Replace(":", "x", StringComparison.OrdinalIgnoreCase)
            .Replace("*", "x", StringComparison.OrdinalIgnoreCase);

        var width = string.IsNullOrWhiteSpace(size) ? null : new ImageRequest { Size = size }.GetImageWidth();
        var height = string.IsNullOrWhiteSpace(size) ? null : new ImageRequest { Size = size }.GetImageHeight();

        if ((width is null || height is null) && !string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            var inferred = request.AspectRatio.InferSizeFromAspectRatio();
            if (inferred is not null)
            {
                width ??= inferred.Value.width;
                height ??= inferred.Value.height;
                warnings.Add(new
                {
                    type = "compatibility",
                    feature = "aspectRatio",
                    details = $"No size provided. Inferred {width}x{height} from aspect ratio '{request.AspectRatio}'."
                });
            }
        }

        width = NormalizeToMultipleOf16(width, "width", warnings);
        height = NormalizeToMultipleOf16(height, "height", warnings);

        return (width, height);
    }

    private static int? NormalizeToMultipleOf16(int? value, string label, List<object> warnings)
    {
        if (value is null)
            return null;

        var normalized = Math.Max(16, (value.Value / 16) * 16);
        if (normalized != value.Value)
        {
            warnings.Add(new
            {
                type = "compatibility",
                feature = label,
                details = $"{label} must be a multiple of 16; adjusted {value.Value} to {normalized}."
            });
        }

        return normalized;
    }

    private sealed class Flux2KleinRequest
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = null!;

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }

        [JsonPropertyName("num_steps")]
        public int? NumSteps { get; set; }

        [JsonPropertyName("guidance")]
        public float? Guidance { get; set; }

        [JsonPropertyName("seed")]
        public int? Seed { get; set; }

        [JsonPropertyName("input_images")]
        public List<string>? InputImages { get; set; }

        [JsonPropertyName("enable_safety_checker")]
        public bool? EnableSafetyChecker { get; set; }

        [JsonPropertyName("output_format")]
        public string? OutputFormat { get; set; }

        [JsonPropertyName("output_quality")]
        public int? OutputQuality { get; set; }

        [JsonPropertyName("enable_base64_output")]
        public bool EnableBase64Output { get; set; }
    }
}

