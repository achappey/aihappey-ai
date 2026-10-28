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

namespace AIHappey.Core.Providers.Munito;

public sealed partial class MunitoProvider : IModelProvider
{
    // All Munito inference APIs (including Anthropic Messages) live under /inference/v1.
    private const string GlobalBaseUrl = "https://api.munito.ai/inference/v1/";
    private const string EuBaseUrl = "https://api.eu.munito.ai/inference/v1/";
    private const string MessagesPath = "messages";
    private const string ChatCompletionsPath = "chat/completions";
    private const string ResponsesPath = "responses";

    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;
    private readonly AsyncCacheHelper _memoryCache;

    public MunitoProvider(
        IApiKeyResolver keyResolver,
        AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri(GlobalBaseUrl);
    }

    public string GetIdentifier() => nameof(Munito).ToLowerInvariant();

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Munito)} API key.");

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

        ApplyAuthHeader();
        return await this.GetChatCompletion(_client, options, relativeUrl: ChatCompletionsPath,
            cancellationToken: cancellationToken);
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

        ApplyAuthHeader();
        await foreach (var update in this.GetChatCompletions(_client, options, relativeUrl: ChatCompletionsPath,
                           cancellationToken: cancellationToken).WithCancellation(cancellationToken))
            yield return update;
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

        ApplyAuthHeader();
        return await this.GetResponse(_client, options, relativeUrl: ResponsesPath,
            cancellationToken: cancellationToken);
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

        ApplyAuthHeader();
        await foreach (var update in this.GetResponses(_client, options, relativeUrl: ResponsesPath,
                           cancellationToken: cancellationToken).WithCancellation(cancellationToken))
            yield return update;
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

        ApplyAuthHeader();
        this.SetDefaultMessagesProperties(request);
        return await this.GetMessage(_client, request, relativeUrl: MessagesPath,
            headers: GetMessagesHeaders(headers), cancellationToken: cancellationToken);
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

        ApplyAuthHeader();
        this.SetDefaultMessagesProperties(request);
        await foreach (var part in this.GetMessages(_client, request, relativeUrl: MessagesPath,
                           headers: GetMessagesHeaders(headers), cancellationToken: cancellationToken)
                           .WithCancellation(cancellationToken))
            yield return part;
    }

    // Munito authenticates with its own key; gateway credentials must never be forwarded.
    private static Dictionary<string, string> GetMessagesHeaders(Dictionary<string, string>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers ?? [])
        {
            if (name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
                || name.Equals("api-key", StringComparison.OrdinalIgnoreCase))
                continue;

            result[name] = value;
        }

        result.TryAdd("anthropic-version", "2023-06-01");
        return result;
    }

    private async Task<bool> IsSpecialModelAsync(string? model, CancellationToken cancellationToken)
        => IsOcrModel(model) || await this.IsTranscriptionModelAsync(model, cancellationToken);

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsOcrModel(request.Model))
            return await ExecuteOcrUnifiedAsync(request, cancellationToken);

        if (await this.IsTranscriptionModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken);

        return await this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsOcrModel(request.Model))
        {
            await foreach (var item in StreamOcrUnifiedAsync(request, cancellationToken))
                yield return item;

            yield break;
        }

        var stream = await this.IsTranscriptionModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedTranscriptionAsync(request, cancellationToken)
            : this.StreamUnifiedViaResponsesAsync(request, cancellationToken);

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;
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
        => throw new NotSupportedException("Munito does not document image generation.");

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document speech generation.");

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document reranking.");

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document realtime.");

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document speech generation.");

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document speech generation.");

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document image generation.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document image generation.");

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document image editing.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document image editing.");

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document video generation.");

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document video generation.");

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document embeddings.");

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document embeddings.");

    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document realtime.");

    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document realtime.");

    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document realtime.");

    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document speech generation.");
}
