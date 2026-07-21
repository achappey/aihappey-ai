using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Ideogram;

public partial class IdeogramProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
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

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
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

    public Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(
        OpenAIImageVariationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageVariationRequest();
        options.Model = ResolveOpenAIModel(options.Model);
        return this.FromImageRequest(options, cancellationToken);
    }

    private static string ResolveOpenAIModel(string? model)
        => string.IsNullOrWhiteSpace(model) ? "ideogram/ideogram-v4" : model.Trim();
}
