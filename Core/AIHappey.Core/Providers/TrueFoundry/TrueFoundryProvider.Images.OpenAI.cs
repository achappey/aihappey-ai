using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.TrueFoundry;

public partial class TrueFoundryProvider
{

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleImageGenerationRequestAsync(
            options,
            endpoint: "images/generations",
            cancellationToken);
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleImageGenerationStreamingAsync(
            options,
            endpoint: "images/generations",
            cancellationToken);
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await _client.OpenAICompatibleImageEditRequestAsync(
            options,
            endpoint: "images/edits",
            cancellationToken);
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleImageEditStreamingAsync(
            options,
            endpoint: "images/edits",
            cancellationToken);
    }
}
