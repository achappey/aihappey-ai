using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Infermatic;

public sealed partial class InfermaticProvider
{
    private async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsyncCore(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client,
            ToNativeEmbeddingRequest(request),
            endpoint: "v1/embeddings",
            cancellationToken: cancellationToken);
        return result.Response;
    }

    private async Task<EmbeddingResponse> EmbeddingRequestAsyncCore(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client,
            ToNativeEmbeddingRequest(openAIRequest),
            endpoint: "v1/embeddings",
            cancellationToken: cancellationToken);
        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());
    }

    private OpenAIEmbeddingRequest ToNativeEmbeddingRequest(OpenAIEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new OpenAIEmbeddingRequest
        {
            Model = StripProviderPrefix(request.Model),
            Input = request.Input,
            Dimensions = request.Dimensions,
            EncodingFormat = request.EncodingFormat,
            User = request.User,
            AdditionalProperties = request.AdditionalProperties
        };
    }

    private string StripProviderPrefix(string model)
        => model.StartsWith($"{GetIdentifier()}/", StringComparison.OrdinalIgnoreCase)
            ? model[(GetIdentifier().Length + 1)..]
            : model;
}
