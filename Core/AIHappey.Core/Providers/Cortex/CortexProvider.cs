using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;

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

        return await _client.GetChatCompletion(
             requestOptions,
             relativeUrl: GetBackendUrl(route.Backend, "v1/chat/completions"),
             ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var route = ResolveRoute(options.Model);
        var requestOptions = CloneWithModel(options, route.ModelId);

        return _client.GetChatCompletionUpdates(
                    requestOptions,
                    relativeUrl: GetBackendUrl(route.Backend, "v1/chat/completions"),
                    ct: cancellationToken);
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

    public Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}
