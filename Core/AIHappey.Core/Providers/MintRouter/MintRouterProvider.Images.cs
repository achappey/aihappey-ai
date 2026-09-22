using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.MintRouter;

public partial class MintRouterProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = new OpenAIImageGenerationRequest
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size
        };
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        var images = response.Data?
            .Select(item => !string.IsNullOrWhiteSpace(item.B64Json)
                ? item.B64Json!.ToDataUrl("image/png")
#pragma warning disable CS0618
                : item.Url)
#pragma warning restore CS0618
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList() ?? [];

        return new ImageResponse
        {
            Images = images,
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, "v1/images/generations", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var item in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(options, "v1/images/generations", cancellationToken))
            yield return item;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditRequestAsync(options, "v1/images/edits", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var item in _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(options, "v1/images/edits", cancellationToken))
            yield return item;
    }
}
