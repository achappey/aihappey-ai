using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.Vultr;

public partial class VultrProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        if (request.Seed.HasValue) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var files = request.Files?.ToArray() ?? [];
        OpenAIImagesResponse response;
        if (files.Length > 0 || request.Mask is not null)
        {
            if (files.Length == 0)
                throw new ArgumentException("Vultr image masks require at least one source image.", nameof(request));

            response = await OpenAIImageEditRequestAsync(new OpenAIImageEditRequest
            {
                Model = request.Model,
                Prompt = request.Prompt,
                N = request.N,
                Size = request.Size,
                ImageFiles = files.Select(CreateVultrImageFile).ToArray(),
                MaskFile = request.Mask is null ? null : CreateVultrImageFile(request.Mask)
            }, cancellationToken);
        }
        else
        {
            response = await OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
            {
                Model = request.Model, Prompt = request.Prompt, N = request.N,
                Size = request.Size, ResponseFormat = "b64_json"
            }, cancellationToken);
        }

        var images = response.Data?.Select(x => !string.IsNullOrWhiteSpace(x.B64Json)
            ? $"data:image/png;base64,{x.B64Json}" : x.Url).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList() ?? [];
        if (images.Count == 0) throw new InvalidOperationException("Vultr image response contained no images.");

        return new ImageResponse
        {
            Images = images, Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(response),
            Response = new() { Timestamp = response.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime : DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }


    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAIImageGenerationRequest();
     
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, "v1/images/generations", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
       
        ApplyAuthHeader();
        await foreach (var item in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(options, "v1/images/generations", cancellationToken))
            yield return item;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditRequestAsync(options, "v1/images/edits", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        await foreach (var item in _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(options, "v1/images/edits", cancellationToken))
            yield return item;
    }

    private static IFormFile CreateVultrImageFile(ImageFile image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is ImageFileUrl || string.Equals(image.Type, "url", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Vultr image edits require uploaded image data; URL image references are not supported.");
        if (string.IsNullOrWhiteSpace(image.Data))
            throw new ArgumentException("Vultr image edit data is required.", nameof(image));
        if (string.IsNullOrWhiteSpace(image.MediaType))
            throw new ArgumentException("Vultr image edit mediaType is required.", nameof(image));

        var encoded = image.Data;
        var marker = encoded.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (marker < 0)
                throw new ArgumentException("Vultr image edits require base64 image data.", nameof(image));
            encoded = encoded[(marker + ";base64,".Length)..];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Vultr image edit data must be valid base64.", nameof(image), exception);
        }

        return new FormFile(new MemoryStream(bytes, writable: false), 0, bytes.Length, "image", "image")
        {
            Headers = new HeaderDictionary(),
            ContentType = image.MediaType
        };
    }

}
