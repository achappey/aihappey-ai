using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LMNT;

public sealed partial class LMNTProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public LMNTProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.lmnt.com/");
    }

    public string GetIdentifier() => "lmnt";

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No LMNT API key.");
        var message = new HttpRequestMessage(method, path);
        message.Headers.Add("X-API-Key", key);
        message.Headers.Add("lmnt-version", "1.2");
        return message;
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
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
        => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
