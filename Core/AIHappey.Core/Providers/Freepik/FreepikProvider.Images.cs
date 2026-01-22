using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        // Classic Fast (sync base64 response)
        if (imageRequest.Model.Equals("classic-fast", StringComparison.OrdinalIgnoreCase))
            return await ClassicFastImageRequest(imageRequest, cancellationToken);

        // Text-to-image generation models (async tasks)
        if (imageRequest.Model.Equals("flux-2-pro", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("flux-2-turbo", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("flux-dev", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("flux-pro-v1-1", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("hyperflux", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("seedream", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("seedream-v4", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("seedream-v4-edit", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("seedream-v4-5", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("z-image", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("z-image-turbo", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("seedream-v4-5-edit", StringComparison.OrdinalIgnoreCase)
            // Mystic models are exposed as mystic/<model>
            || imageRequest.Model.StartsWith("mystic/", StringComparison.OrdinalIgnoreCase))
        {
            return await ImageGenerationImageRequest(imageRequest, cancellationToken);
        }

        if (imageRequest.Model.StartsWith("image-expand/", StringComparison.OrdinalIgnoreCase))
            return await ImageExpandImageRequest(imageRequest, cancellationToken);

        if (imageRequest.Model.StartsWith("skin-enhancer/", StringComparison.OrdinalIgnoreCase))
            return await SkinEnhancerImageRequest(imageRequest, cancellationToken);

        if (imageRequest.Model.Equals("image-relight", StringComparison.OrdinalIgnoreCase))
            return await RelightImageRequest(imageRequest, cancellationToken);

        if (imageRequest.Model.Equals("reimagine-flux", StringComparison.OrdinalIgnoreCase))
            return await ReimagineFluxImageRequest(imageRequest, cancellationToken);

        if (imageRequest.Model.Equals("image-upscaler", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("image-upscaler-precision", StringComparison.OrdinalIgnoreCase)
            || imageRequest.Model.Equals("image-upscaler-precision-v2", StringComparison.OrdinalIgnoreCase))
            return await UpscalerImageRequest(imageRequest, cancellationToken);

        return await IconGenerationImageRequest(imageRequest, cancellationToken);
    }

}

