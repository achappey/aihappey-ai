using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.KalpaLabs;

public partial class KalpaLabsProvider : IModelProvider, IUnifiedModelProvider
{
    private const string ProviderId = "kalpalabs";
    private const string ProviderName = "KalpaLabs";
    private readonly IApiKeyResolver _keyResolver;
    private readonly AsyncCacheHelper _cache;
    private readonly HttpClient _client;

    public KalpaLabsProvider(IApiKeyResolver keyResolver, AsyncCacheHelper cache, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _cache = cache;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.kalpalabs.ai/");
    }

    public string GetIdentifier() => ProviderId;

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(ProviderId), cancellationToken)).ToChatCompletion();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var id = $"chatcmpl_{Guid.NewGuid():N}";
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(ProviderId), cancellationToken))
        {
            var update = part.ToChatCompletionUpdate();
            update.Id = id;
            update.Model = options.Model;
            if (part.Event.Data is AITextDeltaEventData text)
                update.Choices = [new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta { Content = text.Delta }, FinishReason = (string?)null }];
            yield return update;
        }
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(ProviderId), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(ProviderId))
                yield return uiPart;
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest request, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(ProviderId), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamImmutableSpeechToolsAsync(request.ToUnifiedRequest(ProviderId), cancellationToken)
            .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(ProviderId), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request, Dictionary<string, string> headers, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamImmutableSpeechToolsAsync(request.ToUnifiedRequest(ProviderId), cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    private static NotSupportedException Unsupported() => new("KalpaLabs supports speech and conversational speech generation only.");

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
}
