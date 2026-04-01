using AIHappey.Common.Model;
using Microsoft.Extensions.Logging;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
    : IModelProvider
{
    private readonly AsyncCacheHelper _memoryCache;
    private readonly ILogger<GoogleAIProvider> _logger;
    private readonly IApiKeyResolver _keyResolver;

    public GoogleAIProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        ILogger<GoogleAIProvider> logger)
    {
        _keyResolver = keyResolver;
        _logger = logger;
        _memoryCache = asyncCacheHelper;
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

}
