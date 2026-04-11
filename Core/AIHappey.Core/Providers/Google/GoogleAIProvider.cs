using AIHappey.Common.Model;
using Microsoft.Extensions.Logging;
using AIHappey.ChatCompletions.Models;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
    : IModelProvider
{
    private readonly AsyncCacheHelper _memoryCache;
    private readonly ILogger<GoogleAIProvider> _logger;
    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public GoogleAIProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        ILogger<GoogleAIProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _logger = logger;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    }


    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Google)} API key.");

        _client.DefaultRequestHeaders.Remove("x-goog-api-key");
        _client.DefaultRequestHeaders.Add("x-goog-api-key", key);
    }


    private readonly string FILES_API = "https://generativelanguage.googleapis.com/v1beta/files";

    private Mscc.GenerativeAI.GoogleAI GetClient()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Google)} API key.");

        return new(key, logger: _logger);
    }

    private static readonly string Google = "Google";



    public Task<UIMessagePart> CompleteAsync(ChatCompletionOptions request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => GoogleExtensions.Identifier();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
