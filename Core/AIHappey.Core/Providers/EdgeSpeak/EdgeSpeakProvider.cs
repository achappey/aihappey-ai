using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
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
using Microsoft.Extensions.Options;

namespace AIHappey.Core.Providers.EdgeSpeak;

public sealed partial class EdgeSpeakProvider : IModelProvider, IUnifiedModelProvider
{
    private const string ProviderId = "edgespeak";
    private readonly IApiKeyResolver _keys;
    private readonly AsyncCacheHelper _cache;
    private readonly HttpClient _client;

    public EdgeSpeakProvider(IApiKeyResolver keys, AsyncCacheHelper cache, IHttpClientFactory factory,
        IOptions<EdgeSpeakProviderOptions> options)
    {
        _keys = keys;
        _cache = cache;
        _client = factory.CreateClient();
        var endpoint = options.Value.Endpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address) ||
            address.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(address.UserInfo) ||
            !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
            throw new ArgumentException("EdgeSpeak Endpoint must be an absolute HTTP(S) base URL.");
        var baseUrl = address.AbsoluteUri.TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.Ordinal))
            baseUrl = baseUrl[..^3];
        _client.BaseAddress = new Uri(baseUrl + "/");
    }

    public string GetIdentifier() => ProviderId;

    private void ApplyAuthHeader()
    {
        var key = _keys.Resolve(ProviderId);
        _client.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(key)
            ? null : new AuthenticationHeaderValue("Bearer", key);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyAuthHeader();
        return this.GetChatCompletion(_client, options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyAuthHeader();
        return this.GetChatCompletions(_client, options, cancellationToken: cancellationToken);
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyAuthHeader();
        return this.GetResponse(_client, options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyAuthHeader();
        return this.GetResponses(_client, options, cancellationToken: cancellationToken);
    }

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyAuthHeader();
        return this.GetMessage(_client, request, headers: MessageHeaders(headers), cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyAuthHeader();
        return this.GetMessages(_client, request, headers: MessageHeaders(headers), cancellationToken: cancellationToken);
    }

    private static Dictionary<string, string> MessageHeaders(Dictionary<string, string> headers)
    {
        var result = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        result.TryAdd("anthropic-version", "2023-06-01");
        return result;
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(ParseSpeechModel(request.Model!).Model, cancellationToken);
        return model.Type switch
        {
            "speech" => await ExecuteSpeechUnified(request, cancellationToken),
            "transcription" => await ExecuteTranscriptionUnified(request, cancellationToken),
            _ => await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken)
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(ParseSpeechModel(request.Model!).Model, cancellationToken);
        var stream = model.Type switch
        {
            "speech" => StreamSpeechUnified(request, cancellationToken),
            "transcription" => StreamTranscriptionUnified(request, cancellationToken),
            _ => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
        };
        await foreach (var part in stream.WithCancellation(cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(ProviderId), cancellationToken))
            foreach (var ui in part.Event.ToUIMessagePart(ProviderId))
                yield return ui;
    }

    private static NotSupportedException Unsupported() => new("EdgeSpeak does not expose this capability through the unified provider contracts.");

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

public sealed class EdgeSpeakProviderOptions
{
    public string Endpoint { get; set; } = "http://127.0.0.1:1118/";
}
