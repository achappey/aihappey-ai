using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SubQ;

public sealed partial class SubQProvider : IModelProvider
{
    private const string BaseUrl = "https://api.subq.ai/v1/";
    private const string ChatCompletionsPath = "chat/completions";
    private const string ResponsesPath = "responses";

    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;
    private readonly AsyncCacheHelper _memoryCache;

    public SubQProvider(
        IApiKeyResolver keyResolver,
        AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri(BaseUrl);
    }

    public string GetIdentifier() => nameof(SubQ).ToLowerInvariant();

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(SubQ)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureGenerativeModel(options.Model);
        options.Model = StripProviderPrefix(options.Model);
        ApplyAuthHeader();
        NormalizeChatOptions(options);
        try
        {
            return await this.GetChatCompletion(_client, options, relativeUrl: ChatCompletionsPath,
                cancellationToken: cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw WithEarlyAccessHint(exception);
        }
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureGenerativeModel(options.Model);
        options.Model = StripProviderPrefix(options.Model);
        ApplyAuthHeader();
        NormalizeChatOptions(options);

        await using var enumerator = this.GetChatCompletions(_client, options, relativeUrl: ChatCompletionsPath,
            cancellationToken: cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await enumerator.MoveNextAsync())
                    yield break;
            }
            catch (HttpRequestException exception)
            {
                throw WithEarlyAccessHint(exception);
            }

            yield return enumerator.Current;
        }
    }

    public async Task<ResponseResult> ResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        EnsureGenerativeModel(options.Model);
        options.Model = StripProviderPrefix(options.Model);
        ApplyAuthHeader();
        try
        {
            return await this.GetResponse(_client, options, relativeUrl: ResponsesPath,
                cancellationToken: cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw WithEarlyAccessHint(exception);
        }
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureGenerativeModel(options.Model);
        options.Model = StripProviderPrefix(options.Model);
        ApplyAuthHeader();

        await using var enumerator = this.GetResponses(_client, options, relativeUrl: ResponsesPath,
            cancellationToken: cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await enumerator.MoveNextAsync())
                    yield break;
            }
            catch (HttpRequestException exception)
            {
                throw WithEarlyAccessHint(exception);
            }

            yield return enumerator.Current;
        }
    }

    public async Task<MessagesResponse> MessagesAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken);
        return response.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.StreamUnifiedViaResponsesAsync(request, cancellationToken);
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());
        await foreach (var item in StreamUnifiedAsync(unifiedRequest, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            foreach (var part in item.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
        }
    }

    // Decision models only serve /systemone; chat and responses reject them before any network call.
    private static void EnsureGenerativeModel(string? model)
    {
        if (IsDecisionModel(model))
            throw new NotSupportedException(
                $"SubQ model '{StripProviderPrefix(model)}' is a decision model; use the Decisions API instead of chat or responses.");
    }

    // SubQ documents max_tokens only and says not to send max_completion_tokens.
    private static void NormalizeChatOptions(ChatCompletionOptions options)
    {
        var extra = options.AdditionalProperties;
        if (extra is null || !extra.Remove("max_completion_tokens", out var value))
            return;

        extra.TryAdd("max_tokens", value);
    }

    // Chat Completions and Responses are early access; a 401/403/404 may mean the key is not enrolled.
    private static HttpRequestException WithEarlyAccessHint(HttpRequestException exception)
        => exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            ? new HttpRequestException(
                $"{exception.Message} SubQ Chat Completions and Responses are in early access; the API key may not be enabled for this endpoint or model.",
                exception,
                exception.StatusCode)
            : exception;

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document image generation.");

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document transcription.");

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document speech generation.");

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document reranking.");

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document realtime.");

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document speech generation.");

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document speech generation.");

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document image generation.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document image generation.");

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document image editing.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document image editing.");

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document transcription.");

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document transcription.");

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document transcription.");

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document video generation.");

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document video generation.");

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document embeddings.");

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document embeddings.");

    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document realtime.");

    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document realtime.");

    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document realtime.");

    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SubQ does not document speech generation.");
}
