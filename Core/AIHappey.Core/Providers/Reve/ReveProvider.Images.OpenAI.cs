using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Reve;

public partial class ReveProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();

        var result = await ImageRequest(options.ToImageRequest(
            ResolveOpenAIModel(options.Model),
            GetIdentifier()), cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();

        var result = await ImageRequest(options.ToImageRequest(
            ResolveOpenAIModel(options.Model),
            GetIdentifier()), cancellationToken);
        foreach (var streamEvent in result.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();

        var request = await options.ToImageRequest(
            ResolveOpenAIModel(options.Model),
            GetIdentifier(),
            cancellationToken);
        var result = await ImageRequest(request, cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();

        var request = await options.ToImageRequest(
            ResolveOpenAIModel(options.Model),
            GetIdentifier(),
            cancellationToken);
        var result = await ImageRequest(request, cancellationToken);
        foreach (var streamEvent in result.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(OpenAIImageVariationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageVariationRequest();

        var request = await options.ToImageRequest(
            ResolveOpenAIModel(options.Model),
            GetIdentifier(),
            cancellationToken);
        var result = await ImageRequest(request, cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    private static string ResolveOpenAIModel(string? model)
        => string.IsNullOrWhiteSpace(model) || model.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? "reve-v2-create@260601"
            : model.Trim();
}

