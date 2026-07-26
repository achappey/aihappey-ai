using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Renderful;

public partial class RenderfulProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        List<object> warnings = [];
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Renderful image inputs require model-specific fields passed through providerOptions.renderful." });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", details = "Renderful image masks require model-specific fields passed through providerOptions.renderful." });

        var payload = CreateRenderfulPayload(request.ProviderOptions, new Dictionary<string, object?>
        {
            ["type"] = "text-to-image",
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = request.N,
            ["seed"] = request.Seed,
            ["size"] = request.Size,
            ["aspect_ratio"] = request.AspectRatio
        });

        var generation = await CreateGenerationAsync(payload, cancellationToken);
        List<string> images = [];
        foreach (var output in generation.Outputs)
        {
            var (bytes, mediaType) = await DownloadOutputAsync(output, MediaTypeNames.Image.Png, cancellationToken);
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
        }

        if (images.Count == 0)
            throw new InvalidOperationException("Renderful image generation completed without output images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = CreateRenderfulMetadata(generation.Root),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Renderful does not document an OpenAI-compatible image edit contract. Use providerOptions.renderful for a model-specific generation request.");
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Renderful does not document an OpenAI-compatible image edit contract.");
    }

}
