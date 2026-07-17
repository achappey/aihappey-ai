using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Sampling.Mapping;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using AIHappey.Unified.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.UncloseAI;

public partial class UncloseAIProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
    IHttpClientFactory httpClientFactory) : IModelProvider
{
    private const string HermesRoute = "hermes";
    private const string QwenRoute = "qwen";
    private const string ChatCompletionsPath = "v1/chat/completions";
    private static readonly Uri HermesBaseUri = new("https://hermes.ai.unturf.com/");
    private static readonly Uri QwenBaseUri = new("https://qwen.ai.unturf.com/");

    private readonly IApiKeyResolver _keyResolver = keyResolver;

    private readonly HttpClient _client = httpClientFactory.CreateClient();

    private readonly AsyncCacheHelper _memoryCache = asyncCacheHelper;

    private static string BuildUrl(Uri baseUri, string relativePath)
        => new Uri(baseUri, relativePath).ToString();

    private (string Route, Uri BaseUri, string UpstreamModelId) ResolveRoute(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("UncloseAI model id is required.");

        var (provider, providerLocalModelId) = modelId.SplitModelId();

        var effectiveModelId = string.Equals(provider, GetIdentifier(), StringComparison.OrdinalIgnoreCase)
            ? providerLocalModelId
            : modelId;

        var segments = effectiveModelId.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
            throw new InvalidOperationException("UncloseAI model ids must be formatted as 'uncloseai/hermes/<model>' or 'uncloseai/qwen/<model>'.");

        var route = segments[0];
        var upstreamModelId = segments[1];

        if (string.IsNullOrWhiteSpace(upstreamModelId))
            throw new InvalidOperationException("UncloseAI model ids must include the upstream model name after the route segment.");

        return route.ToLowerInvariant() switch
        {
            HermesRoute => (route, HermesBaseUri, upstreamModelId),
            QwenRoute => (route, QwenBaseUri, upstreamModelId),
            _ => throw new InvalidOperationException("UncloseAI model ids must use either 'hermes' or 'qwen' as the route segment.")
        };
    }

    private static ChatCompletionOptions CloneOptionsWithModel(ChatCompletionOptions options, string upstreamModelId)
    {
        return new ChatCompletionOptions
        {
            Model = upstreamModelId,
            Temperature = options.Temperature,
            ParallelToolCalls = options.ParallelToolCalls,
            Stream = options.Stream,
            Messages = options.Messages?.ToArray() ?? [],
            Tools = options.Tools?.ToArray() ?? [],
            ToolChoice = options.ToolChoice,
            ResponseFormat = options.ResponseFormat,
            Store = options.Store
        };
    }

    private static ChatRequest CloneRequestWithModel(ChatRequest chatRequest, string upstreamModelId)
    {
        return new ChatRequest
        {
            Id = chatRequest.Id,
            Messages = chatRequest.Messages?.ToList() ?? [],
            Model = upstreamModelId,
            ToolChoice = chatRequest.ToolChoice,
            MaxToolCalls = chatRequest.MaxToolCalls,
            Temperature = chatRequest.Temperature,
            TopP = chatRequest.TopP,
            MaxOutputTokens = chatRequest.MaxOutputTokens,
            Tools = chatRequest.Tools?.ToList() ?? [],
            ProviderMetadata = chatRequest.ProviderMetadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ResponseFormat = chatRequest.ResponseFormat
        };
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var (Route, BaseUri, UpstreamModelId) = ResolveRoute(options.Model);
        var requestOptions = CloneOptionsWithModel(options, UpstreamModelId);

        return await this.GetChatCompletion(_client,
             requestOptions,
             relativeUrl: BuildUrl(BaseUri, ChatCompletionsPath),
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var route = ResolveRoute(options.Model);
        var requestOptions = CloneOptionsWithModel(options, route.UpstreamModelId);

        return this.GetChatCompletions(_client,
                    requestOptions,
                    relativeUrl: BuildUrl(route.BaseUri, ChatCompletionsPath),
                    cancellationToken: cancellationToken);
    }

    public string GetIdentifier() => nameof(UncloseAI).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToSamplingResult();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()),
           cancellationToken);

        return result.ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            yield return part.ToResponseStreamPart();
        }

        yield break;
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }


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
            cancellationToken))
        {
            foreach (var item in part.ToMessageStreamParts())
                yield return item;
        }

        yield break;
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
         => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(OpenAIImageVariationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    private sealed class UncloseAiModelsResponse
    {
        [JsonPropertyName("data")]
        public List<UncloseAiModelEntry>? Data { get; set; }
    }

    private sealed class UncloseAiModelEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("owned_by")]
        public string? OwnedBy { get; set; }
    }
}
