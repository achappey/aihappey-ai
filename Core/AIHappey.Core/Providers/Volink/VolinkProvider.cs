using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using ResponsesRequest = AIHappey.Responses.ResponseRequest;
using ResponsesResult = AIHappey.Responses.ResponseResult;

namespace AIHappey.Core.Providers.Volink;

public partial class VolinkProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;
    private readonly AsyncCacheHelper _memoryCache;

    public VolinkProvider(
        IApiKeyResolver keyResolver,
        AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.volink.org/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Volink)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => nameof(Volink).ToLowerInvariant();

    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return await this.GetChatCompletion(_client, options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetChatCompletions(_client, options, cancellationToken: cancellationToken);
    }

    public async Task<ResponsesResult> ResponsesAsync(
        ResponsesRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return await this.GetResponse(_client, options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<AIHappey.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponsesRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetResponses(_client, options, cancellationToken: cancellationToken);
    }

    public async Task<MessagesResponse> MessagesAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return await this.GetMessage(_client, request, headers: headers, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetMessages(_client, request, headers: headers, cancellationToken: cancellationToken);
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await this.IsImageModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedImageAsync(request, cancellationToken);

        if (await this.IsSpeechModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedSpeechAsync(request, cancellationToken);

        return await this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IAsyncEnumerable<AIStreamEvent> stream =
            await this.IsImageModelAsync(request.Model, cancellationToken)
                ? this.StreamUnifiedImageAsync(request, cancellationToken)
                : await this.IsSpeechModelAsync(request.Model, cancellationToken)
                    ? this.StreamUnifiedSpeechAsync(request, cancellationToken)
                    : this.StreamUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = chatRequest.ToUnifiedRequest(GetIdentifier());
        await foreach (var part in this.StreamUnifiedAsync(request, cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    public Task<RealtimeResponse> GetRealtimeToken(
        RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document a realtime API.");

    public Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document a transcription API.");

    public Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document a reranking API.");

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document a transcription API.");

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document a transcription API.");

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document a transcription API.");

    public Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document a video API.");

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document a video API.");

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document an embeddings API.");

    public Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document an embeddings API.");

    public Task<LiveSessionResponse> CreateLiveSessionAsync(
        LiveSessionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document a realtime API.");

    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(
        string model,
        RealtimeTokenRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document a realtime API.");

    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(
        string model,
        RealtimeSessionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document a realtime API.");

    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Volink does not document a native streaming speech API.");
}
