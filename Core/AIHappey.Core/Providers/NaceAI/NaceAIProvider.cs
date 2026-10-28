using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NaceAI;

public sealed partial class NaceAIProvider : IModelProvider
{
    private const string BaseUrl = "https://console.nace.ai/";

    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AsyncCacheHelper _memoryCache;

    public NaceAIProvider(
        IApiKeyResolver keyResolver,
        AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _httpClientFactory = httpClientFactory;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri(BaseUrl);
    }

    public string GetIdentifier() => nameof(NaceAI).ToLowerInvariant();

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(NaceAI)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (await IsSpecialModelAsync(options.Model, cancellationToken))
        {
            var unified = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);
            return unified.ToChatCompletion();
        }

        throw UnsupportedChatModel(options.Model);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (await IsSpecialModelAsync(options.Model, cancellationToken))
        {
            await foreach (var streamEvent in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                               .WithCancellation(cancellationToken))
                yield return streamEvent.ToChatCompletionUpdate();

            yield break;
        }

        throw UnsupportedChatModel(options.Model);
    }

    public async Task<ResponseResult> ResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        if (await IsSpecialModelAsync(options.Model, cancellationToken))
        {
            var unified = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);
            return unified.ToResponseResult();
        }

        throw UnsupportedChatModel(options.Model);
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (await IsSpecialModelAsync(options.Model, cancellationToken))
        {
            await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                               .ToResponseStreamParts(cancellationToken))
                yield return part;

            yield break;
        }

        throw UnsupportedChatModel(options.Model);
    }

    public async Task<MessagesResponse> MessagesAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        if (await IsSpecialModelAsync(request.Model, cancellationToken))
        {
            var unified = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken);
            return unified.ToMessagesResponse();
        }

        throw UnsupportedChatModel(request.Model);
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (await IsSpecialModelAsync(request.Model, cancellationToken))
        {
            await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                               .ToMessageStreamParts(request.Model, cancellationToken))
                yield return part;

            yield break;
        }

        throw UnsupportedChatModel(request.Model);
    }

    private static NotSupportedException UnsupportedChatModel(string? model)
        => new($"NaceAI model '{model}' is not a chat model. Use a document special model ({string.Join(", ", DocumentModels.Keys)}), '{BrowserRunModel}', or the Decisions endpoint.");

    private Task<bool> IsSpecialModelAsync(string? model, CancellationToken cancellationToken)
        => Task.FromResult(!IsDecisionOnlyModel(model));

    // Every NaceAI chat-style model is a special model; Drex models are only reachable through Decisions.
    private static bool IsDecisionOnlyModel(string? model)
        => NormalizeModelId(model).StartsWith("drex-", StringComparison.OrdinalIgnoreCase)
           && !IsDocumentModel(model) && !IsBrowserRunModel(model);

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsBrowserRunModel(request.Model))
            return ExecuteBrowserRunUnifiedAsync(request, cancellationToken);
        if (IsDocumentModel(request.Model))
            return ExecuteDocumentUnifiedAsync(request, cancellationToken);

        throw UnsupportedChatModel(request.Model);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IAsyncEnumerable<AIStreamEvent> stream;
        if (IsBrowserRunModel(request.Model))
            stream = StreamBrowserRunUnifiedAsync(request, cancellationToken);
        else if (IsDocumentModel(request.Model))
            stream = StreamDocumentUnifiedAsync(request, cancellationToken);
        else
            throw UnsupportedChatModel(request.Model);

        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
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

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document image generation.");

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document speech generation.");

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document reranking.");

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document realtime.");

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document speech generation.");

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document speech generation.");

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document image generation.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document image generation.");

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document image editing.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document image editing.");

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document video generation.");

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document video generation.");

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document embeddings.");

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document embeddings.");

    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document realtime.");

    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document realtime.");

    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document realtime.");

    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document speech generation.");

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document transcription.");

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document transcription.");

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document transcription.");

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NaceAI does not document transcription.");
}
