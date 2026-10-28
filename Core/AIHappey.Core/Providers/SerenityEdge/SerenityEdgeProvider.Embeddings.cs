using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SerenityEdge;

public sealed partial class SerenityEdgeProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEmbeddingLimits(request);
        ApplyAuthHeader();
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client, request, cancellationToken: cancellationToken);
        return result.Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        if (openAIRequest.AdditionalProperties?.TryGetValue("encoding_format", out var encodingFormat) == true)
        {
            if (encodingFormat.ValueKind != System.Text.Json.JsonValueKind.String
                || encodingFormat.GetString() is not ("float" or "base64"))
                throw new ArgumentException("providerOptions.serenityedge.encoding_format must be 'float' or 'base64'.", nameof(request));
            openAIRequest.EncodingFormat = encodingFormat.GetString();
            openAIRequest.AdditionalProperties.Remove("encoding_format");
        }
        ValidateEmbeddingLimits(openAIRequest);
        ApplyAuthHeader();
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client, openAIRequest, cancellationToken: cancellationToken);
        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());
    }

    private static void ValidateEmbeddingLimits(OpenAIEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(request);
        if (request.Dimensions is not null)
            throw new NotSupportedException("Serenity Edge embeddings always return vectors with 1024 dimensions.");
        if (request.User is not null || request.AdditionalProperties is { Count: > 0 })
            throw new NotSupportedException("Serenity Edge embeddings do not document user or additional request options.");

        var count = request.Input.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => 1,
            System.Text.Json.JsonValueKind.Array when request.Input.GetArrayLength() > 0
                && request.Input[0].ValueKind == System.Text.Json.JsonValueKind.Number => 1,
            System.Text.Json.JsonValueKind.Array => request.Input.GetArrayLength(),
            _ => 0
        };
        if (count > 512)
            throw new ArgumentException("Serenity Edge embeddings support at most 512 inputs per request.", nameof(request));
    }
}
