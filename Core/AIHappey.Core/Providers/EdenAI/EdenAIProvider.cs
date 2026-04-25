using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Responses.Extensions;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Sampling.Mapping;
using System.Text.Json;
using AIHappey.Responses.Streaming;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.EdenAI;

public partial class EdenAIProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public EdenAIProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.edenai.run/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(EdenAI)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetChatCompletion(
             _client,
             options,
             relativeUrl: "v3/chat/completions",
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetChatCompletions(
                    _client,
                    options,
                    relativeUrl: "v3/chat/completions",
                    cancellationToken: cancellationToken);
    }

    public string GetIdentifier() => nameof(EdenAI).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var result = await this.ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToSamplingResult();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var response = await this.GetResponse(_client,
                   options,
                   relativeUrl: "v3/responses",
                   cancellationToken: cancellationToken);

        if (response.AdditionalProperties?.TryGetValue("cost", out var costObj) == true
                 && TryGetDecimal(costObj, out var cost))
        {
            response.Metadata ??= [];

            response.Metadata.Add("gateway", JsonSerializer.SerializeToElement(new
            {
                cost
            }));
        }

        return response;
    }

    static bool TryGetDecimal(object? value, out decimal result)
    {
        switch (value)
        {
            case decimal d:
                result = d;
                return true;

            case double db:
                result = (decimal)db;
                return true;

            case float f:
                result = (decimal)f;
                return true;

            case string s when decimal.TryParse(s, out var parsed):
                result = parsed;
                return true;

            case JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetDecimal(out var jeDec):
                result = jeDec;
                return true;

            default:
                result = default;
                return false;
        }
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
     ResponseRequest options,
     [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var part in this.GetResponses(
            _client,
            options,
            relativeUrl: "v3/responses",
            cancellationToken: cancellationToken))
        {
            // pass through everything by default
            if (part is ResponseCompleted completed)
            {
                EnrichCompleted(completed);
                yield return completed;
                continue;
            }

            yield return part;
        }
    }

    private void EnrichCompleted(ResponseCompleted completed)
    {
        var response = completed.Response;
        if (response == null) return;

        if (completed.AdditionalProperties?.TryGetValue("cost", out var costObj) == true
            && TryGetDecimal(costObj, out var cost))
        {
            response.Metadata ??= [];

            response.Metadata.Add("gateway", JsonSerializer.SerializeToElement(new
            {
                cost
            }));
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
        ApplyAuthHeader();

        return await this.GetMessage(_client,
                   request,
                   relativeUrl: "v3/v1/messages",
                   headers: headers,
                   cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetMessages(_client,
           request,
           relativeUrl: "v3/v1/messages",
           headers: headers,
           cancellationToken: cancellationToken);
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
          => this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);

}