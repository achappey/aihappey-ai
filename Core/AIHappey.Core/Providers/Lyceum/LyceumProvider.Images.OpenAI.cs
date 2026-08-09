using AIHappey.Core.Models;
using AIHappey.Core.Extensions;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Lyceum;

public partial class LyceumProvider
{


    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
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

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();

        var response = await ImageRequest(
            await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken),
            cancellationToken);

        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();

        var response = await ImageRequest(
            await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken),
            cancellationToken);

        foreach (var streamEvent in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

}
