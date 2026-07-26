using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Runtime.CompilerServices;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.AlphaNeural;

public partial class AlphaNeuralProvider
{
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();

        return _client.OpenAICompatibleImageGenerationRequestAsync(options, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();

        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options,
            cancellationToken: cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("AlphaNeural does not document OpenAI-compatible image edit support.");
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("AlphaNeural does not document OpenAI-compatible image edit streaming support.");
    }

}
