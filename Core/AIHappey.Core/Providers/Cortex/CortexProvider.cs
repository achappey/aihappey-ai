using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using System.Runtime.CompilerServices;
using AIHappey.Unified.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Cortex;

public partial class CortexProvider : IModelProvider
{
    private const string DefaultBackend = "api";

    private static readonly IReadOnlyDictionary<string, Uri> BackendBaseUris = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
    {
        [DefaultBackend] = new("https://api.claude.gg/"),
        ["claude"] = new("https://claude.gg/"),
        ["codex"] = new("https://codex.claude.gg/")
    };

    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public CortexProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.claude.gg/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Cortex)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var route = ResolveRoute(options.Model);
        var requestOptions = CloneWithModel(options, route.ModelId);

        return await this.GetChatCompletion(_client,
             requestOptions,
             relativeUrl: GetBackendUrl(route.Backend, "v1/chat/completions"),
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var route = ResolveRoute(options.Model);
        var requestOptions = CloneWithModel(options, route.ModelId);

        return this.GetChatCompletions(_client,
                    requestOptions,
                    relativeUrl: GetBackendUrl(route.Backend, "v1/chat/completions"),
                    cancellationToken: cancellationToken);
    }

    public string GetIdentifier() => nameof(Cortex).ToLowerInvariant();

    private static ChatCompletionOptions CloneWithModel(ChatCompletionOptions options, string modelId)
        => new()
        {
            Model = modelId,
            Temperature = options.Temperature,
            ParallelToolCalls = options.ParallelToolCalls,
            Stream = options.Stream,
            Messages = options.Messages,
            Tools = options.Tools,
            ToolChoice = options.ToolChoice,
            ResponseFormat = options.ResponseFormat
        };

    private static ChatRequest CloneWithModel(ChatRequest chatRequest, string modelId)
        => new()
        {
            Id = chatRequest.Id,
            Messages = chatRequest.Messages,
            Model = modelId,
            ToolChoice = chatRequest.ToolChoice,
            MaxToolCalls = chatRequest.MaxToolCalls,
            Temperature = chatRequest.Temperature,
            TopP = chatRequest.TopP,
            MaxOutputTokens = chatRequest.MaxOutputTokens,
            Tools = chatRequest.Tools,
            ProviderMetadata = chatRequest.ProviderMetadata,
            ResponseFormat = chatRequest.ResponseFormat
        };

    private BackendRoute ResolveRoute(string? exposedModelId)
    {
        if (string.IsNullOrWhiteSpace(exposedModelId))
            throw new ArgumentException("Model id is required.", nameof(exposedModelId));

        var trimmed = exposedModelId.Trim();
        var providerPrefix = GetIdentifier() + "/";

        if (!trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            return CreateRoute(DefaultBackend, trimmed);

        var routedModel = trimmed[providerPrefix.Length..].Trim();

        foreach (var backend in BackendBaseUris.Keys)
        {
            var backendPrefix = backend + "/";
            if (routedModel.StartsWith(backendPrefix, StringComparison.OrdinalIgnoreCase))
                return CreateRoute(backend, routedModel[backendPrefix.Length..]);
        }

        return CreateRoute(DefaultBackend, routedModel);
    }

    private static BackendRoute CreateRoute(string backend, string modelId)
    {
        if (!BackendBaseUris.TryGetValue(backend, out var baseUri))
            throw new NotSupportedException($"Cortex backend '{backend}' is not supported.");

        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model id is required.", nameof(modelId));

        return new BackendRoute(backend, baseUri, modelId.Trim());
    }

    private static string GetBackendUrl(string backend, string relativePath)
    {
        if (!BackendBaseUris.TryGetValue(backend, out var baseUri))
            throw new NotSupportedException($"Cortex backend '{backend}' is not supported.");

        return new Uri(baseUri, relativePath).ToString();
    }

    private string ToBackendModelId(string backend, string modelId)
        => $"{GetIdentifier()}/{backend}/{modelId}";

    private sealed record BackendRoute(string Backend, Uri BaseUri, string ModelId);

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
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

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
        {
            yield return part.ToResponseStreamPart();
        }
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
}
