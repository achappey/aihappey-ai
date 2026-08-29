using AIHappey.Core.AI;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Messages.Mapping;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Responses.Mapping;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.PaxaLabs;

public partial class PaxaLabsProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public PaxaLabsProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.paxalabs.com/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(PaxaLabs)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        return (await ExecuteUnifiedAsync(
             options.ToUnifiedRequest(GetIdentifier()),
             cancellationToken))
             .ToChatCompletion();
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
                           unifiedRequest,
                           cancellationToken))
            yield return part.ToChatCompletionUpdate();
    }

    public string GetIdentifier() => nameof(PaxaLabs).ToLowerInvariant();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(
        Responses.ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        return (await ExecuteUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
            .ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
                           unifiedRequest,
                           cancellationToken)
                           .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();



    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = request.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = NormalizePaxaModel(request.Model);
        return model switch
        {
            TranslationModelId => ExecuteTranslationAsync(request, cancellationToken),
            OcrModelId => ExecuteOcrAsync(request, cancellationToken),
            _ => throw new NotSupportedException($"Paxa Labs model '{request.Model}' is not a unified text or OCR model.")
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        foreach (var item in response.Output?.Items ?? [])
        {
            foreach (var text in (item.Content ?? []).OfType<AITextContentPart>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Guid.NewGuid().ToString("n");
                var timestamp = DateTimeOffset.UtcNow;
                yield return CreatePaxaStreamEvent(id, "text-start", new AITextStartEventData(), timestamp, item.Metadata);
                if (!string.IsNullOrEmpty(text.Text))
                    yield return CreatePaxaStreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = text.Text }, timestamp, item.Metadata);
                yield return CreatePaxaStreamEvent(id, "text-end", new AITextEndEventData(), timestamp, item.Metadata);
            }
        }

        var completedAt = DateTimeOffset.UtcNow;
        yield return CreatePaxaStreamEvent(request.Id ?? Guid.NewGuid().ToString("n"), "finish", new AIFinishEventData
        {
            FinishReason = "stop", Model = response.Model, CompletedAt = completedAt.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? request.Model ?? string.Empty, completedAt, response.Usage)
        }, completedAt, response.Metadata, response.Output);
    }

    private static string NormalizePaxaModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        var normalized = model.Trim();
        const string providerPrefix = "paxalabs/";
        if (normalized.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)) normalized = normalized[providerPrefix.Length..];
        return normalized;
    }

    private AIStreamEvent CreatePaxaStreamEvent(string id, string type, object data, DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata, AIOutput? output = null) => new()
    {
        ProviderId = GetIdentifier(), Metadata = metadata,
        Event = new AIEventEnvelope { Id = id, Type = type, Timestamp = timestamp, Data = data, Metadata = metadata, Output = output }
    };

 

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



    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}
