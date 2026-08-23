using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client,
            request,
            endpoint: "v3/compat/embeddings",
            cancellationToken: cancellationToken);

        return result.Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client,
            openAIRequest,
            endpoint: "v3/compat/embeddings",
            cancellationToken: cancellationToken);

        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());
    }
}
