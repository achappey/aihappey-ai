using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Orq;

public partial class OrqProvider : IModelProvider
{
    private const string ProviderId = "orq";
    private const string ProviderName = "Orq";

    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public OrqProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.orq.ai/v2/router/");
    }

    public string GetIdentifier() => ProviderId;

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {ProviderName} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return Task.FromResult<IEnumerable<Model>>([]);

        return Task.FromResult<IEnumerable<Model>>(GetIdentifier().GetModels());
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return await _client.GetChatCompletion(options, ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.GetChatCompletionUpdates(options, ct: cancellationToken);
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken = default)
        => this.ChatCompletionsSamplingAsync(chatRequest, cancellationToken);

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest, cancellationToken: cancellationToken))
            yield return update;
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return await _client.GetResponses(options, ct: cancellationToken);
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.GetResponsesUpdates(options, ct: cancellationToken);
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
