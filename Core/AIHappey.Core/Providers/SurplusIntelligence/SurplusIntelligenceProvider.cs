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
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SurplusIntelligence;

public sealed partial class SurplusIntelligenceProvider(
    IApiKeyResolver keyResolver,
    IHttpClientFactory httpClientFactory,
    AsyncCacheHelper memoryCache) : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api.surplusintelligence.ai/");
        return client;
    }

    public string GetIdentifier() => "surplusintelligence";

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Surplus Intelligence API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        options.Model = GetNativeModel(options.Model
            ?? throw new ArgumentException("A model is required.", nameof(options)));
        return this.GetChatCompletion(_client, options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        options.Model = GetNativeModel(options.Model
            ?? throw new ArgumentException("A model is required.", nameof(options)));
        return this.GetChatCompletions(_client, options, cancellationToken: cancellationToken);
    }

    public Task<ResponseResult> ResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        options.Model = GetNativeModel(options.Model
            ?? throw new ArgumentException("A model is required.", nameof(options)));
        return this.GetResponse(_client, options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        options.Model = GetNativeModel(options.Model
            ?? throw new ArgumentException("A model is required.", nameof(options)));
        return this.GetResponses(_client, options, cancellationToken: cancellationToken);
    }

    public Task<MessagesResponse> MessagesAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        request.Model = GetNativeModel(request.Model);
        return this.GetMessage(_client, request, headers: headers, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        request.Model = GetNativeModel(request.Model);
        return this.GetMessages(_client, request, headers: headers, cancellationToken: cancellationToken);
    }

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        request.Model = GetNativeModel(request.Model);
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client, request, cancellationToken: cancellationToken);
        return result.Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var native = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        native.Model = GetNativeModel(native.Model);
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client, native, cancellationToken: cancellationToken);
        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());
    }

    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
        => SendOpenAIDecisionAsync(request, cancellationToken);

    public Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
        => SendDecisionAsync(request, cancellationToken);

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
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
        var stream = await this.IsVideoModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedVideoAsync(request, cancellationToken: cancellationToken)
            : await this.IsTranscriptionModelAsync(request.Model, cancellationToken)
                ? this.StreamUnifiedTranscriptionAsync(request, cancellationToken)
                : await this.IsSpeechModelAsync(request.Model, cancellationToken)
                    ? this.StreamUnifiedSpeechAsync(request, cancellationToken)
                    : await this.IsImageModelAsync(request.Model, cancellationToken)
                        ? this.StreamUnifiedImageAsync(request, cancellationToken)
                        : this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

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

    public Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => CreateTranscriptionAsync(request, cancellationToken);

    public Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
        => CreateSpeechAsync(request, cancellationToken);

    public Task<ImageResponse> ImageRequest(
        ImageRequest request,
        CancellationToken cancellationToken = default)
        => CreateImageAsync(request, cancellationToken);

    public Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Surplus Intelligence does not document reranking.");

    public Task<RealtimeResponse> GetRealtimeToken(
        RealtimeRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Surplus Intelligence does not document realtime sessions.");

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        options.Model = GetNativeModel(options.Model);
        return _client.OpenAICompatibleSpeechRequestAsync(options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        options.Model = GetNativeModel(options.Model);
        return _client.OpenAICompatibleStreamingSpeechAsync(options, cancellationToken: cancellationToken);
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        options.Model = GetNativeModel(options.Model);
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        options.Model = GetNativeModel(options.Model);
        return _client.OpenAICompatibleImageGenerationStreamingAsync(options, cancellationToken: cancellationToken);
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Surplus Intelligence documents image generation, not image editing.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Surplus Intelligence documents image generation, not image editing.");

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        options.Model = GetNativeModel(options.Model);
        return _client.OpenAICompatibleTranscriptionRequestAsync(options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        options.Model = GetNativeModel(options.Model);
        return _client.OpenAICompatibleTranscriptionStreamingAsync(options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        request.Model = GetNativeModel(request.Model);
        return _client.OpenAICompatibleVercelTranscriptionStreamingAsync(
            request, GetIdentifier(), cancellationToken: cancellationToken);
    }

    public Task<LiveSessionResponse> CreateLiveSessionAsync(
        LiveSessionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Surplus Intelligence does not document live sessions.");

    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(
        string model,
        RealtimeTokenRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Surplus Intelligence does not document realtime sessions.");

    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(
        string model,
        RealtimeSessionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Surplus Intelligence does not document realtime sessions.");

    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Surplus Intelligence does not document Vercel speech streaming.");
}
