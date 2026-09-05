using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LibertAI;

public partial class LibertAIProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client, request, "v1/embeddings", cancellationToken);
        return result.Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        ApplyAuthHeader();
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client, openAIRequest, "v1/embeddings", cancellationToken);
        return result.ToEmbeddingResponse(
            GetIdentifier().CreatePrimitiveProviderMetadata(result.Response));
    }
}
