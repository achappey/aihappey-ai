using System.Net.Mime;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PreAPI;

public partial class PreAPIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = $"PreAPI currently returns a single primary generation per request. Requested n={request.N}." });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var input = BuildImageInput(request, warnings);
        using var doc = await GenerateAsync(request.Model, input, cancellationToken);

        var data = GetResponseData(doc.RootElement);
        var output = GetOutput(data);
        var imageItems = GetImageOutputs(output);

        List<string> images = [];
        foreach (var imageItem in imageItems)
        {
            var url = imageItem.Url;
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var (Base64, MediaType) = await DownloadMediaAsync(url, imageItem.ContentType, cancellationToken);
            images.Add(Base64.ToDataUrl(MediaType));
        }

        if (images.Count == 0 && TryGetString(data, "output_url") is { } fallbackUrl)
        {
            var (Base64, MediaType) = await DownloadMediaAsync(fallbackUrl, MediaTypeNames.Image.Png, cancellationToken);
            images.Add(Base64.ToDataUrl(MediaType));
        }

        if (images.Count == 0)
            throw new InvalidOperationException("PreAPI image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = CreateProviderMetadata(doc.RootElement),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = doc.RootElement.Clone()
            }
        };
    }
}
