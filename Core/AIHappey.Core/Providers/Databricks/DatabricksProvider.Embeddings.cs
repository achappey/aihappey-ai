using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Databricks;

public partial class DatabricksProvider
{

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client,
            request,
            endpoint: "ai-gateway/mlflow/v1/embeddings",
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
            endpoint: "ai-gateway/mlflow/v1/embeddings",
            cancellationToken: cancellationToken);

        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());
    }
}