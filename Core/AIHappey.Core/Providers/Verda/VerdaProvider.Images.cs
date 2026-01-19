using AIHappey.Common.Model;
using AIHappey.Core.ModelProviders;


namespace AIHappey.Core.Providers.Verda;

public partial class VerdaProvider
    : IModelProvider
{
    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var model = imageRequest.Model;

        return model switch
        {
            "flux-dev" => Flux1ImageRequest(imageRequest, cancellationToken),
            "flux-krea-dev" => Flux1KreaImageRequest(imageRequest, cancellationToken),
            "flux-kontext-max" or "flux-kontext-pro" => Flux1KontextImageRequest(imageRequest, cancellationToken),
            "flux-kontext-dev" => Flux1KontextDevImageRequest(imageRequest, cancellationToken),
            "flux2-dev" or "flux-2-flex" or "flux-2-pro" => Flux2ImageRequest(imageRequest, cancellationToken),
            "flux2-klein-4b" or "flux2-klein-base-4b" or "flux2-klein-base-9b" => Flux2KleinImageRequest(imageRequest, cancellationToken),
            _ => throw new NotSupportedException($"Verda image model '{model}' is not supported.")
        };
    }
}

