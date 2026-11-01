using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.InferexAI;

public sealed partial class InferexAIProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var result = await SendEmbedding(request, cancellationToken);
        return result.Mapped.Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var result = await SendEmbedding(request.ToOpenAIEmbeddingRequest(ProviderId), cancellationToken);
        return result.Mapped.ToEmbeddingResponse(Metadata(result.Raw));
    }

    private async Task<(OpenAICompatibleEmbeddingResult Mapped, JsonElement Raw)> SendEmbedding(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken)
    {
        ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(request);
        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/embeddings")
        {
            Content = new StringContent(JsonSerializer.Serialize(NativeEmbedding(request)), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(message, cancellationToken);
        var root = await ReadJson(response, cancellationToken);
        var mapped = root.Deserialize<OpenAIEmbeddingResponse>() ?? throw new InvalidOperationException("InferexAI returned an empty embedding response.");
        mapped.Model = $"{ProviderId}/{NativeModel(mapped.Model)}";
        return (new(mapped, response.GetHeaders()), root);
    }

    private static OpenAIEmbeddingRequest NativeEmbedding(OpenAIEmbeddingRequest request)
        => new()
        {
            Model = NativeModel(request.Model), Input = request.Input, Dimensions = request.Dimensions,
            EncodingFormat = request.EncodingFormat, User = request.User, AdditionalProperties = request.AdditionalProperties
        };
}
