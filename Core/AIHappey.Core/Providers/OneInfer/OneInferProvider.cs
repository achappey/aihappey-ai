using AIHappey.Core.AI;
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
using System.Text.Json;

namespace AIHappey.Core.Providers.OneInfer;

public partial class OneInferProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private string? _accessTokenApiKey;

    public OneInferProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.oneinfer.ai/");
    }

    private async Task ApplyAuthHeaderAsync(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(OneInfer)} API key.");

        if (!string.Equals(_accessTokenApiKey, key, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(_accessToken))
        {
            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                if (!string.Equals(_accessTokenApiKey, key, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(_accessToken))
                {
                    using var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        $"v1/ula/oauth-authentication?api_key={Uri.EscapeDataString(key)}");
                    using var response = await _client.SendAsync(request, cancellationToken);
                    var raw = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException($"OneInfer authentication failed ({(int)response.StatusCode}): {raw}");

                    using var document = JsonDocument.Parse(raw);
                    if (!document.RootElement.TryGetProperty("access_token", out var tokenElement)
                        || string.IsNullOrWhiteSpace(tokenElement.GetString()))
                    {
                        throw new InvalidOperationException("OneInfer authentication response contained no access_token.");
                    }

                    _accessToken = tokenElement.GetString();
                    _accessTokenApiKey = key;
                }
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        await ApplyAuthHeaderAsync(cancellationToken);

        return await this.GetChatCompletion(_client,
             options,
             relativeUrl: "v1/ula/chat/completions",
             cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ApplyAuthHeaderAsync(cancellationToken);

        await foreach (var update in this.GetChatCompletions(_client,
                    options,
                    relativeUrl: "v1/ula/chat/completions",
                    cancellationToken: cancellationToken))
            yield return update;
    }

    public string GetIdentifier() => nameof(OneInfer).ToLowerInvariant();

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

}
