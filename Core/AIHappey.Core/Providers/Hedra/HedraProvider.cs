using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Hedra;

public partial class HedraProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly AsyncCacheHelper _memoryCache;
    private readonly HttpClient _client;
    private readonly HttpClient _llmClient;
    private readonly HttpClient _mediaClient;

    public HedraProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.hedra.com/");
        _llmClient = httpClientFactory.CreateClient();
        _llmClient.BaseAddress = new Uri("https://api.hedra.com/");
        // Presigned output URLs must never receive API credentials.
        _mediaClient = httpClientFactory.CreateClient();
    }

    public string GetIdentifier() => "hedra";

    private void ApplyAuthHeader(HttpClient client, string scheme)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Hedra API key.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, key);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(_llmClient, "Bearer");
        return this.GetChatCompletion(_llmClient, options, relativeUrl: "v3/chat/completions", cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(_llmClient, "Bearer");
        return this.GetChatCompletions(_llmClient, options, relativeUrl: "v3/chat/completions", cancellationToken: cancellationToken);
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(_llmClient, "Bearer");
        return this.GetResponse(_llmClient, options, relativeUrl: "v3/responses", cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(_llmClient, "Bearer");
        return this.GetResponses(_llmClient, options, relativeUrl: "v3/responses", cancellationToken: cancellationToken);
    }

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(_llmClient, "Bearer");
        return this.GetMessage(_llmClient, request, relativeUrl: "v3/anthropic/v1/messages", headers: headers, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(_llmClient, "Bearer");
        return this.GetMessages(_llmClient, request, relativeUrl: "v3/anthropic/v1/messages", headers: headers, cancellationToken: cancellationToken);
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var type = (await this.GetModel(request.Model, cancellationToken)).Type;
        return type switch
        {
            "video" => await this.ExecuteUnifiedVideoAsync(request, cancellationToken: cancellationToken),
            "image" => await this.ExecuteUnifiedImageAsync(request, cancellationToken),
            "speech" => await this.ExecuteUnifiedSpeechAsync(request, cancellationToken),
            _ => await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var type = (await this.GetModel(request.Model, cancellationToken)).Type;
        var stream = type switch
        {
            "video" => this.StreamUnifiedVideoAsync(request, cancellationToken: cancellationToken),
            "image" => this.StreamUnifiedImageAsync(request, cancellationToken),
            "speech" => this.StreamUnifiedSpeechAsync(request, cancellationToken),
            _ => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
        };
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
