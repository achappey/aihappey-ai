using System.Net.Http.Headers;
using AIHappey.Core.ModelProviders;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model.Responses;
using AIHappey.Common.Model.Responses.Streaming;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider : IModelProvider
{
    private readonly HttpClient _client;
    private readonly IApiKeyResolver _keyResolver;

    public FreepikProvider(HttpClient client, IApiKeyResolver keyResolver)
    {
        _client = client;
        _keyResolver = keyResolver;
    }

    public string GetIdentifier() => nameof(Freepik).ToLowerInvariant();

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default)
        => ModelProviderImageExtensions.StreamImageAsync(this, chatRequest, cancellationToken);

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => SoundEffectsSpeechRequest(request, cancellationToken);

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    private string GetKey() => _keyResolver.Resolve(GetIdentifier()) ?? throw new InvalidOperationException("No Freepik API key configured.");

    private void ApplyAuthHeader()
    {
        _client.DefaultRequestHeaders.Remove("x-freepik-api-key");
        _client.DefaultRequestHeaders.Add("x-freepik-api-key", GetKey());
        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}

