using ModelContextProtocol.Protocol;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Core.ModelProviders;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Bria;

public partial class BriaProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public BriaProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://engine.prod.bria-api.com/v2/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Bria)} API key.");

        // Bria expects `api_token: <token>` header (per Bria docs).
        _client.DefaultRequestHeaders.Remove("api_token");
        _client.DefaultRequestHeaders.Add("api_token", key);

        // Ensure no bearer header is accidentally reused from other providers.
        _client.DefaultRequestHeaders.Authorization = null;
    }



    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => nameof(Bria).ToLowerInvariant();


    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => await this.ImageSamplingAsync(chatRequest, cancellationToken: cancellationToken);


    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
