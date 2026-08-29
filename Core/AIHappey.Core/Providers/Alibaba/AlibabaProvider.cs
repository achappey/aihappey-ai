using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using AIHappey.Responses.Extensions;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public AlibabaProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://dashscope-intl.aliyuncs.com/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Alibaba (DashScope) API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => "alibaba";

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetChatCompletion(_client,
             options,
             relativeUrl: "compatible-mode/v1/chat/completions",
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetChatCompletions(_client,
                    options,
                    relativeUrl: "compatible-mode/v1/chat/completions",
                    cancellationToken: cancellationToken);
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => TranscriptionRequestInternal(imageRequest, cancellationToken);


    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetResponse(_client,
                   options,
                   relativeUrl: "compatible-mode/v1/responses",
                   cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetResponses(_client,
           options,
           relativeUrl: "compatible-mode/v1/responses",
           cancellationToken: cancellationToken);
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetMessage(_client,
           request,
           relativeUrl: "apps/anthropic/v1/messages",
           headers,
           cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetMessages(_client,
           request,
           relativeUrl: "apps/anthropic/v1/messages",
           headers,
           cancellationToken: cancellationToken);
    }


    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => await this.IsVideoModelAsync(request.Model, cancellationToken)
            ? await this.ExecuteUnifiedVideoAsync(request, cancellationToken: cancellationToken)
            : await this.IsTranscriptionModelAsync(request.Model, cancellationToken)
            ? await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken: cancellationToken)
            : await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stream = await this.IsVideoModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedVideoAsync(request, cancellationToken: cancellationToken)
            : await this.IsTranscriptionModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedTranscriptionAsync(request, cancellationToken: cancellationToken)
            : this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;
    }

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

