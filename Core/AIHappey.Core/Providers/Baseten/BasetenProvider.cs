using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using OpenAI.Responses;

namespace AIHappey.Core.Providers.Baseten;

/// <summary>
/// Baseten Inference (OpenAI-compatible Chat Completions).
/// Base URL: https://inference.baseten.co/v1/
/// - POST chat/completions
/// </summary>
public sealed partial class BasetenProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://inference.baseten.co/v1/");
        return client;
    }

    public string GetIdentifier() => "baseten";

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Baseten API key.");

        // Baseten expects: Authorization: Api-Key <key>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Api-Key", key);
    }

    // Responses API is not implemented for Baseten in this repo.
    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

