using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Verda;

public partial class VerdaProvider
    : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public VerdaProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://inference.datacrunch.io/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Verda)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => nameof(Verda).ToLowerInvariant();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => await this.ImageSamplingAsync(chatRequest, cancellationToken);

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var p in this.StreamImageAsync(chatRequest, cancellationToken))
            yield return p;
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

