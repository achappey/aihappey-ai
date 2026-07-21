using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Core.Models;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Cartesia;

public partial class CartesiaProvider : IModelProvider, IUnifiedModelProvider
{
    private const string ProviderId = "cartesia";
    private const string ProviderName = "Cartesia";
    private const string DefaultApiVersion = "2026-03-01";

    private static readonly string[] SupportedTtsModelIds =
    [
        "sonic-3.5",
        "sonic-3",
        "sonic-latest"
    ];

    private static readonly string[] SupportedTranscriptionModelIds =
    [
        "ink-whisper",
        "ink-whisper-2025-06-04"
    ];

    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public CartesiaProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.cartesia.ai/");
    }

    public string GetIdentifier() => ProviderId;

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {ProviderName} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static void ApplyVersionHeader(HttpRequestMessage request, string? apiVersion)
    {
        request.Headers.Remove("Cartesia-Version");
        request.Headers.TryAddWithoutValidation("Cartesia-Version", string.IsNullOrWhiteSpace(apiVersion) ? DefaultApiVersion : apiVersion.Trim());
    }

    private bool IsTranscriptionModel(string model)
    {
        if (model.StartsWith("transcription/", StringComparison.OrdinalIgnoreCase))
            return true;

        return SupportedTranscriptionModelIds.Any(m =>
            string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsTranscriptionModel(request.Model!))
            return this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken);

        throw new NotSupportedException("Cartesia supports only transcription models on Unified AI conversations.");
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsTranscriptionModel(request.Model!))
            throw new NotSupportedException("Cartesia supports only transcription models on Unified AI conversations.");

        await foreach (var streamEvent in this.StreamUnifiedTranscriptionAsync(request, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsTranscriptionModel(chatRequest.Model))
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
            yield return p;
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        if (IsTranscriptionModel(options.Model!))
            throw new NotSupportedException("Cartesia transcription is not supported on Responses API.");

        return await this.SpeechResponseAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }


    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(OpenAIImageVariationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

}