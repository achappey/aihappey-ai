using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Verda;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Verda;

public partial class VerdaProvider
    : IModelProvider
{
    private const string Flux2DevEndpoint = "https://inference.datacrunch.io/flux2-dev/runsync";
    private const string Flux2FlexEndpoint = "https://relay.datacrunch.io/bfl/flux-2-flex";
    private const string Flux2ProEndpoint = "https://relay.datacrunch.io/bfl/flux-2-pro";

    public async Task<ImageResponse> Flux2ImageRequest(
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
        var flux2 = providerMetadata?.Flux2;

        if (!string.IsNullOrWhiteSpace(imageRequest.Size) || !string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "size",
                details = "Flux.2 does not accept size/aspect ratio in this integration; values were ignored."
            });
        }

        if (imageRequest.N is > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "Flux.2 returns one image per request; generated a single image."
            });
        }

        if (imageRequest.Seed is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed",
                details = "Flux.2 seed is not supported in this integration; value was ignored."
            });
        }

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask",
                details = "Flux.2 does not accept a separate mask input; value was ignored."
            });
        }

        var referenceImages = imageRequest.Files?.Select(ToDataUrl).ToList();
        if (referenceImages?.Count == 0)
            referenceImages = null;

        var outputFormat = "jpeg";
        var endpoint = imageRequest.Model switch
        {
            "flux2-dev" => Flux2DevEndpoint,
            "flux-2-flex" => Flux2FlexEndpoint,
            "flux-2-pro" => Flux2ProEndpoint,
            _ => throw new NotSupportedException($"Verda image model '{imageRequest.Model}' is not supported.")
        };

        var payload = new Flux2Request
        {
            Prompt = imageRequest.Prompt,
            OutputFormat = outputFormat,
            ReferenceImages = referenceImages,
            Steps = flux2?.Steps,
            Guidance = flux2?.Guidance,
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

    private sealed class Flux2Request
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = null!;

        [JsonPropertyName("output_format")]
        public string? OutputFormat { get; set; }

        [JsonPropertyName("reference_images")]
        public List<string>? ReferenceImages { get; set; }

        [JsonPropertyName("steps")]
        public int? Steps { get; set; }

        [JsonPropertyName("guidance")]
        public float? Guidance { get; set; }

        [JsonPropertyName("enable_base64_output")]
        public bool EnableBase64Output { get; set; }
    }
}

