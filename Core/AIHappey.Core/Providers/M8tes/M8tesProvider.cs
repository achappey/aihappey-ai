using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.M8tes;

public partial class M8tesProvider : IModelProvider, IUnifiedModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly IEndUserIdResolver _userResolver;
    private readonly AsyncCacheHelper _memoryCache;
    private readonly HttpClient _client;

    private const string M8tesResolvedUserIdMetadataKey = "m8tes.resolved_user_id";

    public M8tesProvider(IApiKeyResolver keyResolver, IEndUserIdResolver userResolver,
        AsyncCacheHelper asyncCacheHelper, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _userResolver = userResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.m8tes.ai/api/v2/");
    }

    public string GetIdentifier() => "m8tes";

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            .ToChatCompletion();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            yield return part.ToChatCompletionUpdate();
    }

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options,
        CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            .ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            .ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Resolve against the original Vercel request, not a synthetic unified projection.
        var resolvedUserId = _userResolver.Resolve(chatRequest);
        var request = chatRequest.ToUnifiedRequest(GetIdentifier());
        var metadata = request.Metadata is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(request.Metadata);
        // This is internal state: overwrite client input, and retain null as an explicit resolution.
        metadata[M8tesResolvedUserIdMetadataKey] = resolvedUserId;
        var unifiedRequest = new AIRequest
        {
            ProviderId = request.ProviderId,
            Model = request.Model,
            Id = request.Id,
            Instructions = request.Instructions,
            Input = request.Input,
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxOutputTokens,
            MaxToolCalls = request.MaxToolCalls,
            Stream = request.Stream,
            ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice,
            ResponseFormat = request.ResponseFormat,
            Tools = request.Tools,
            Metadata = metadata,
            Headers = request.Headers,
            Verbosity = request.Verbosity
        };

        await foreach (var part in StreamUnifiedAsync(unifiedRequest, cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
        }
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
