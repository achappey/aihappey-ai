using System.Net.Http.Headers;
using System.Text.Json;
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
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Keln;

public sealed partial class KelnProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;
    private readonly HttpClient _messagesClient;
    private readonly AsyncCacheHelper _cache;

    public KelnProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory, AsyncCacheHelper cache)
    {
        _keyResolver = keyResolver;
        _cache = cache;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.keln.ai/");
        _messagesClient = httpClientFactory.CreateClient();
        _messagesClient.BaseAddress = new Uri("https://anthropic.keln.ai/");
    }

    public string GetIdentifier() => "keln";

    private void ApplyAuthHeader(bool messages = false)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Keln API key.");
        if (messages)
        {
            _messagesClient.DefaultRequestHeaders.Authorization = null;
            _messagesClient.DefaultRequestHeaders.Remove("x-api-key");
            _messagesClient.DefaultRequestHeaders.Add("x-api-key", key);
            _messagesClient.DefaultRequestHeaders.Remove("anthropic-version");
            _messagesClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }
        else
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private string NativeModel(string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return model.StartsWith("keln/", StringComparison.OrdinalIgnoreCase) ? model[5..] : model;
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        PrepareChat(options, stream: false);
        options.Stream = false;
        ApplyAuthHeader();
        return this.GetChatCompletion(_client, options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        PrepareChat(options, stream: true);
        options.Stream = true;
        ApplyAuthHeader();
        return this.GetChatCompletions(_client, options, cancellationToken: cancellationToken);
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        PrepareResponses(options, stream: false);
        options.Stream = false;
        ApplyAuthHeader();
        return this.GetResponse(_client, options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        PrepareResponses(options, stream: true);
        options.Stream = true;
        ApplyAuthHeader();
        return this.GetResponses(_client, options, cancellationToken: cancellationToken);
    }

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        request = PrepareMessages(request);
        request.Stream = false;
        ApplyAuthHeader(messages: true);
        return this.GetMessage(_messagesClient, request, headers: MessageHeaders(request, headers), cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        request = PrepareMessages(request);
        request.Stream = true;
        ApplyAuthHeader(messages: true);
        return this.GetMessages(_messagesClient, request, headers: MessageHeaders(request, headers), cancellationToken: cancellationToken);
    }

    private static Dictionary<string, string> MessageHeaders(MessagesRequest request, Dictionary<string, string> headers)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in (request.Headers ?? []).Concat(headers))
            if (IsSafeTransportHeader(name)
                && !name.Equals("anthropic-version", StringComparison.OrdinalIgnoreCase))
                merged[name] = value;
        return merged;
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    private static NotSupportedException Unsupported() => new("Keln does not document this gateway capability.");
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken) => throw Unsupported();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
}
