using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Portkey;

public partial class PortkeyProvider
{


    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditRequestAsync(
            options,
           cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(
            options,
           cancellationToken: cancellationToken);
    }

    
}
