using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Mammouth;

public sealed partial class MammouthProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(_client, NativeEmbeddingRequest(request),
            endpoint: "v1/embeddings", cancellationToken: cancellationToken);
        return result.Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(_client,
            NativeEmbeddingRequest(request.ToOpenAIEmbeddingRequest(GetIdentifier())),
            endpoint: "v1/embeddings", cancellationToken: cancellationToken);
        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());
    }

    private OpenAIEmbeddingRequest NativeEmbeddingRequest(OpenAIEmbeddingRequest request)
        => new()
        {
            Model = NativeModel(request.Model),
            Input = request.Input,
            Dimensions = request.Dimensions,
            EncodingFormat = request.EncodingFormat,
            User = request.User,
            AdditionalProperties = request.AdditionalProperties
        };
}
