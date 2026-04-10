using AIHappey.Common.Model;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Core.AI;
using System.Text.Json;
using System.Globalization;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider : IModelProvider
{
    private const decimal UsdTicksPerDollar = 10_000_000_000m;
    private const decimal NanoUsdPerDollar = 1_000_000_000m;

    private readonly HttpClient _client;

    private readonly IApiKeyResolver _keyResolver;

    private readonly AsyncCacheHelper _memoryCache;

    public XAIProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;

        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.x.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(xAI)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => SpeechRequestInternal(imageRequest, cancellationToken);

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
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

    internal static decimal? GetGatewayCost(object? usage)
    {
        if (!TryGetUsageCost(usage, out var cost))
            return null;

        return cost;
    }

    private static bool TryGetUsageCost(object? usage, out decimal cost)
    {
        cost = 0m;
        if (usage == null)
            return false;

        try
        {
            var json = usage is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);

            if (TryGetDecimal(json, "cost_in_usd_ticks", out var usdTicks))
            {
                cost = usdTicks / UsdTicksPerDollar;
                return true;
            }

            if (TryGetDecimal(json, "cost_in_nano_usd", out var nanoUsd))
            {
                cost = nanoUsd / NanoUsdPerDollar;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryGetDecimal(JsonElement json, string key, out decimal value)
    {
        value = 0m;

        if (json.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in json.EnumerateObject())
        {
            if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value.ValueKind switch
            {
                JsonValueKind.Number when property.Value.TryGetDecimal(out var number) => (value = number) >= 0 || number < 0,
                JsonValueKind.String when decimal.TryParse(property.Value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => (value = parsed) >= 0 || parsed < 0,
                _ => false
            };
        }

        return false;
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
     => this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);
}
