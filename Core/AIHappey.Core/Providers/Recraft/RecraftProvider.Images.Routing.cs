using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Recraft;

public partial class RecraftProvider
{
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        ApplyAuthHeader();

        return request.Model.Trim() switch
        {
            "vectorize" => SendMultipartImageRequestAsync(
                request,
                endpoint: "v1/images/vectorize",
                primaryFileField: "file",
                promptRequired: false,
                promptSupported: false,
                maskRequired: false,
                sizeSupported: false,
                defaultMime: "image/svg+xml",
                cancellationToken),

            "generateBackground" => SendMultipartImageRequestAsync(
                request,
                endpoint: "v1/images/generateBackground",
                primaryFileField: "image",
                promptRequired: true,
                promptSupported: true,
                maskRequired: true,
                sizeSupported: false,
                defaultMime: "image/png",
                cancellationToken),

            "removeBackground" => SendMultipartImageRequestAsync(
                request,
                endpoint: "v1/images/removeBackground",
                primaryFileField: "file",
                promptRequired: false,
                promptSupported: false,
                maskRequired: false,
                sizeSupported: false,
                defaultMime: "image/png",
                cancellationToken),

            "crispUpscale" => SendMultipartImageRequestAsync(
                request,
                endpoint: "v1/images/crispUpscale",
                primaryFileField: "file",
                promptRequired: false,
                promptSupported: false,
                maskRequired: false,
                sizeSupported: false,
                defaultMime: "image/png",
                cancellationToken),

            "creativeUpscale" => SendMultipartImageRequestAsync(
                request,
                endpoint: "v1/images/creativeUpscale",
                primaryFileField: "file",
                promptRequired: false,
                promptSupported: false,
                maskRequired: false,
                sizeSupported: false,
                defaultMime: "image/png",
                cancellationToken),

            "eraseRegion" => SendMultipartImageRequestAsync(
                request,
                endpoint: "v1/images/eraseRegion",
                primaryFileField: "image",
                promptRequired: false,
                promptSupported: false,
                maskRequired: true,
                sizeSupported: false,
                defaultMime: "image/png",
                cancellationToken),

            "variateImage" => SendMultipartImageRequestAsync(
                request,
                endpoint: "v1/images/variateImage",
                primaryFileField: "image",
                promptRequired: false,
                promptSupported: false,
                maskRequired: false,
                sizeSupported: true,
                defaultMime: "image/png",
                cancellationToken),

            _ => GenerateImagesAsync(request, request.Model.Trim(), cancellationToken)
        };
    }
}

