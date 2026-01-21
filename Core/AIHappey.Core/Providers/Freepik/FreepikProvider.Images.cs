using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

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

