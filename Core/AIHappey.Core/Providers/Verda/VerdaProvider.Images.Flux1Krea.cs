using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Verda;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Verda;

public partial class VerdaProvider
    : IModelProvider
{
    private const string Flux1KreaEndpoint = "flux-krea-dev/runsync";

    private async Task<ImageResponse> Flux1KreaImageRequest(
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
        var flux1 = providerMetadata?.Flux1;

        if (imageRequest.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Flux.1 Krea does not support input images; files were ignored."
            });
        }

        if (imageRequest.Files?.Skip(1).Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Multiple input images are not supported."
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
            EnableBase64Output = true
        };

        var payload = new Flux1Request { Input = input };
        var json = JsonSerializer.Serialize(payload, Flux1Json);

        using var resp = await _client.PostAsync(
            Flux1KreaEndpoint,
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
}

