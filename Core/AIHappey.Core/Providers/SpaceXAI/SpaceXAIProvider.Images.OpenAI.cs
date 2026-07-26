using AIHappey.Core.Models;
using AIHappey.Core.Extensions;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.SpaceXAI;

public partial class SpaceXAIProvider
{

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAIImageGenerationRequest();

        var response = await ImageRequest(
            options.ToImageRequest(options.Model, GetIdentifier()),
            cancellationToken);

        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAIImageGenerationRequest();

        var response = await ImageRequest(
            options.ToImageRequest(options.Model, GetIdentifier()),
            cancellationToken);

        foreach (var streamEvent in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAIImageEditRequest();

        var request = await options.ToImageRequest(
            options.Model,
            GetIdentifier(),
            cancellationToken);
        var response = await ImageRequest(request, cancellationToken);

        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAIImageEditRequest();

        var request = await options.ToImageRequest(
            options.Model,
            GetIdentifier(),
            cancellationToken);
        var response = await ImageRequest(request, cancellationToken);

        foreach (var streamEvent in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }


}
