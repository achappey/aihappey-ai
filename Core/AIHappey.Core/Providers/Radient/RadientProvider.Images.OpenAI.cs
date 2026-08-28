using System.Runtime.CompilerServices;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Radient;

public partial class RadientProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var payload = CopyMetadata(options.AdditionalProperties);
        payload.Remove("model");
        payload.Remove("provider");
        payload["prompt"] = options.Prompt;
        payload["num_images"] = options.N ?? 1;
        payload["sync_mode"] = false;
        Set(payload, "image_size", ResolveImageSize(options.Size, null));
        var result = await GenerateImagesAsync(payload, cancellationToken);
        var images = await DownloadImagesAsync(result.Images, cancellationToken);
        return ToOpenAIImages(images, options.Size, options.Quality, options.OutputFormat);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        var index = 0;
        foreach (var image in response.Data ?? [])
        {
            yield return new OpenAIImageGenerationPartialImage
            {
                B64Json = image.B64Json ?? "",
                CreatedAt = response.Created,
                PartialImageIndex = index++,
                Size = response.Size,
                Quality = response.Quality,
                OutputFormat = response.OutputFormat
            };
        }
        if (response.Data?.LastOrDefault()?.B64Json is { } last)
            yield return new OpenAIImageGenerationCompleted { B64Json = last, CreatedAt = response.Created };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mask is not null || options.MaskFile is not null)
            throw new NotSupportedException("Radient image generation does not document mask editing.");

        var sourceUrl = options.Images?.FirstOrDefault()?.ImageUrl;
        if (string.IsNullOrWhiteSpace(sourceUrl) && options.ImageFiles?.FirstOrDefault() is { } formFile)
        {
            await using var stream = formFile.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            sourceUrl = $"data:{formFile.ContentType};base64,{Convert.ToBase64String(memory.ToArray())}";
        }
        if (string.IsNullOrWhiteSpace(sourceUrl)) throw new ArgumentException("An input image is required.", nameof(options));

        var payload = CopyMetadata(options.AdditionalProperties);
        payload.Remove("model");
        payload.Remove("provider");
        payload["prompt"] = options.Prompt;
        payload["source_url"] = sourceUrl;
        payload["num_images"] = options.N ?? 1;
        payload["sync_mode"] = false;
        Set(payload, "image_size", ResolveImageSize(options.Size, null));
        var result = await GenerateImagesAsync(payload, cancellationToken);
        var images = await DownloadImagesAsync(result.Images, cancellationToken);
        return ToOpenAIImages(images, options.Size, options.Quality, options.OutputFormat);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        var index = 0;
        foreach (var image in response.Data ?? [])
        {
            yield return new OpenAIImageEditPartialImage
            {
                B64Json = image.B64Json ?? "", CreatedAt = response.Created,
                PartialImageIndex = index++, Size = response.Size,
                Quality = response.Quality, OutputFormat = response.OutputFormat
            };
        }
        if (response.Data?.LastOrDefault()?.B64Json is { } last)
            yield return new OpenAIImageEditCompleted { B64Json = last, CreatedAt = response.Created };
    }

    private static OpenAIImagesResponse ToOpenAIImages(List<string> images, string? size, string? quality, string? outputFormat)
        => new()
        {
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Size = size,
            Quality = quality,
            OutputFormat = outputFormat,
            Data = images.Select(dataUrl => new OpenAIImageData
            {
                B64Json = dataUrl.Contains(";base64,", StringComparison.OrdinalIgnoreCase)
                    ? dataUrl[(dataUrl.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) + 8)..] : dataUrl
            }).ToList()
        };
}
