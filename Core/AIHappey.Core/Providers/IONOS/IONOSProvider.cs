using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Sampling.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Messages.Mapping;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.IONOS;

public partial class IONOSProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public IONOSProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://openai.inference.de-txl.ionos.com/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(IONOS)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => nameof(IONOS).ToLowerInvariant();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()),
           cancellationToken);

        return result.ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            yield return part.ToResponseStreamPart();
        }

        yield break;
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = request.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var item in part.ToMessageStreamParts())
                yield return item;
        }

        yield break;
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
}
