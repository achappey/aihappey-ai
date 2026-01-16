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
    public Task<ImageResponse> Flux1KontextImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => Flux1KontextImageRequestInternal(imageRequest, cancellationToken);

    private async Task<ImageResponse> Flux1KontextImageRequestInternal(
        ImageRequest imageRequest,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var providerMetadata = imageRequest.GetImageProviderMetadata<VerdaImageProviderMetadata>(GetIdentifier());
        var flux1 = providerMetadata?.Flux1;

        if (!string.IsNullOrWhiteSpace(imageRequest.Size) || !string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "size",
                details = "Flux.1 Kontext [pro/max] does not support size or aspect ratio; values were ignored."
            });
        }

        if (imageRequest.N is > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "Flux.1 Kontext [pro/max] returns one image per request; generated a single image."
            });
        }

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

        var outputFormat = flux1?.OutputFormat ?? "jpeg";
        var endpoint = imageRequest.Model switch
        {
            "flux-kontext-pro" => "https://relay.datacrunch.io/bfl/flux-kontext-pro",
            "flux-kontext-max" => "https://relay.datacrunch.io/bfl/flux-kontext-max",
            _ => throw new NotSupportedException($"Verda image model '{imageRequest.Model}' is not supported.")
        };

        var payload = new Flux1KontextRequest
        {
            Prompt = imageRequest.Prompt,
            Steps = flux1?.NumInferenceSteps,
            Guidance = flux1?.GuidanceScale,
            PromptUpsampling = true,
            InputImage = inputImage is null ? null : ToDataUrl(inputImage)
        };

        var json = JsonSerializer.Serialize(payload, Flux1Json);

        using var resp = await _client.PostAsync(
            endpoint,
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var rawBytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var rawText = Encoding.UTF8.GetString(rawBytes);
            throw new Exception(ExtractVerdaError(rawText) ?? $"{resp.StatusCode}: {rawText}");
        }

        var mimeType = MapOutputFormatToMimeType(outputFormat);
        var b64 = Convert.ToBase64String(rawBytes);
        var images = new List<string> { b64.ToDataUrl(mimeType) };

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = new
                {
                    endpoint,
                    status = (int)resp.StatusCode,
                    contentType = resp.Content.Headers.ContentType?.MediaType,
                    byteLength = rawBytes.Length
                }
            }
        };
    }

    private sealed class Flux1KontextRequest
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = null!;

        [JsonPropertyName("steps")]
        public int? Steps { get; set; }

        [JsonPropertyName("guidance")]
        public float? Guidance { get; set; }

        [JsonPropertyName("prompt_upsampling")]
        public bool PromptUpsampling { get; set; }

        [JsonPropertyName("input_image")]
        public string? InputImage { get; set; }
    }
}

