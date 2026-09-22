using Microsoft.Extensions.Logging;
using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Interactions.Mapping;
using AIHappey.Interactions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Common.MCP;

namespace AIHappey.Core.Providers.Google;

[McpServer(
    "AI-Google-Maps",
    "AI Google Maps",
    "Use Google Maps grounding for places, routing, distances, and location context.",
    ["https://upload.wikimedia.org/wikipedia/commons/thumb/a/aa/Google_Maps_icon_%282020%29.svg/1920px-Google_Maps_icon_%282020%29.svg.png"],
    nameof(GoogleMaps_Ask))]
[McpServer(
    "AI-Google-YouTube",
    "AI Google YouTube",
    "Ask Gemini to analyze, summarize, or extract information from a YouTube video.",
    ["https://www.youtube.com/s/desktop/014dbbed/img/favicon_144x144.png"],
    nameof(GoogleYouTube_Ask))]
public partial class GoogleAIProvider
    : IModelProvider, IUnifiedModelProvider, IProviderMcpServers
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

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);
        return result.ToChatCompletion();
    }

    public string GetIdentifier() => GoogleExtensions.Identifier();



    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in StreamUnifiedAsync(
                           options.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken))
        {
            yield return item.ToChatCompletionUpdate();
        }
    }

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var unifiedResponse = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);
        var response = unifiedResponse.ToResponseResult();
        return EnrichResponseWithGatewayCost(response, options.Model, options.ServiceTier);
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
       
        var responseStreamState = new ResponsesUnifiedMapper.ResponseReverseStreamState();

        await foreach (var mappedItem in StreamUnifiedAsync(
                           options.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken))
        {
            Responses.Streaming.ResponseStreamPart part = mappedItem.ToResponseStreamPart(responseStreamState);

            if (part is Responses.Streaming.ResponseCompleted completed)
            {
                part = new Responses.Streaming.ResponseCompleted
                {
                    SequenceNumber = completed.SequenceNumber,
                    Response = EnrichResponseWithGatewayCost(
                        completed.Response,
                        options.Model,
                        options.ServiceTier),
                    AdditionalProperties = completed.AdditionalProperties
                };
            }

            yield return part;
        }
    }



    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var result = await this.ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in this.StreamUnifiedAsync(
            request.ToUnifiedRequest(GetIdentifier()),
            cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

}
