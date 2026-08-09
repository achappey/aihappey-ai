using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.Extensions;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Lumenfall;

public partial class LumenfallProvider
{
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();

        return _client.OpenAICompatibleImageGenerationRequestAsync(
            options,
            "v1/images/generations",
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();

        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options,
            "v1/images/generations",
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();

        return _client.OpenAICompatibleImageEditRequestAsync(
            options,
            "v1/images/edits",
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();

        await foreach (var streamEvent in _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(
            options,
            "v1/images/edits",
            cancellationToken))
        {
            yield return streamEvent;
        }
    }
}
