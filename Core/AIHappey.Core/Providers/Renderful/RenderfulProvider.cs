using AIHappey.Core.AI;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using System.Runtime.CompilerServices;
using AIHappey.Unified.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Renderful;

public partial class RenderfulProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public RenderfulProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.renderful.ai/api/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Renderful)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetChatCompletion(_client,
             options,
             relativeUrl: "v1/generations",
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetChatCompletions(_client,
                    options,
                    relativeUrl: "v1/generations",
                    cancellationToken: cancellationToken);
    }

    public string GetIdentifier() => nameof(Renderful).ToLowerInvariant();

    

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(
          Responses.ResponseRequest options,
          CancellationToken cancellationToken = default)
    {
        return (await ExecuteUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
            .ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
                           unifiedRequest,
                           cancellationToken)
                           .ToResponseStreamParts(cancellationToken))
            yield return part;

        yield break;
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();


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
            cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;

        yield break;
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await this.IsVideoModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedVideoAsync(request, cancellationToken: cancellationToken);
        if (await this.IsTranscriptionModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken);
        if (await this.IsSpeechModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedSpeechAsync(request, cancellationToken);
        if (await this.IsImageModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedImageAsync(request, cancellationToken);

        return await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IAsyncEnumerable<AIStreamEvent> stream = await this.IsVideoModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedVideoAsync(request, cancellationToken: cancellationToken)
            : await this.IsTranscriptionModelAsync(request.Model, cancellationToken)
                ? this.StreamUnifiedTranscriptionAsync(request, cancellationToken)
                : await this.IsSpeechModelAsync(request.Model, cancellationToken)
                    ? this.StreamUnifiedSpeechAsync(request, cancellationToken)
                    : await this.IsImageModelAsync(request.Model, cancellationToken)
                        ? this.StreamUnifiedImageAsync(request, cancellationToken)
                        : this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;
    }

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
