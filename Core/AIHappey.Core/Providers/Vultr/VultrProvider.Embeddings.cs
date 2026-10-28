using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vultr;

public partial class VultrProvider
{
    private async Task<OpenAIEmbeddingResponse> GetOpenAIEmbeddingsAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var result = await _client.OpenAICompatibleEmbeddingRequestAsync(
            request,
            endpoint: "v1/embeddings",
            cancellationToken: cancellationToken);
        return result.Response;
    }

    private async Task<EmbeddingResponse> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client,
            request.ToOpenAIEmbeddingRequest(GetIdentifier()),
            endpoint: "v1/embeddings",
            cancellationToken: cancellationToken);

        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());
    }
}
