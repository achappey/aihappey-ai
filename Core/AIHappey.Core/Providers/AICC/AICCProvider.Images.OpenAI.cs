using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.AICC;

public partial class AICCProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var request = options.ToImageRequest(options.Model, GetIdentifier());
        var response = await ImageRequestAICC(request, cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var request = options.ToImageRequest(options.Model, GetIdentifier());
        var metadata = request.GetProviderMetadata<System.Text.Json.JsonElement>(GetIdentifier());
        var family = ResolveImageFamily(request.Model, false, metadata);

        if (string.Equals(family, "openai", StringComparison.OrdinalIgnoreCase))
        {
            ApplyAuthHeader();
            await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationStreamingAsync(
                options,
                "v1/images/generations",
                cancellationToken))
            {
                yield return streamEvent;
            }

            yield break;
        }

        var response = await ImageRequestAICC(request, cancellationToken);
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
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await ImageRequestAICC(request, cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var metadata = request.GetProviderMetadata<System.Text.Json.JsonElement>(GetIdentifier());
        var family = ResolveImageFamily(request.Model, true, metadata);

        if (string.Equals(family, "openai", StringComparison.OrdinalIgnoreCase))
        {
            ApplyAuthHeader();
            await foreach (var streamEvent in _client.OpenAICompatibleImageEditStreamingAsync(
                options,
                "v1/images/edits",
                cancellationToken))
            {
                yield return streamEvent;
            }

            yield break;
        }

        var response = await ImageRequestAICC(request, cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }
}
