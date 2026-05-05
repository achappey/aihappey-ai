using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Messages.Mapping;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Responses.Mapping;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using AIHappey.Sampling.Mapping;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Brave;

public partial class BraveProvider : IModelProvider
{
    private const string UsageStartTag = "<usage>";
    private const string UsageEndTag = "</usage>";

    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public BraveProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.search.brave.com/res/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Brave)} API key.");

        _client.DefaultRequestHeaders.Remove("x-subscription-token");
        _client.DefaultRequestHeaders.Add("x-subscription-token", key);
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
           => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        options.Messages = [options.Messages.Last(a => a.Role == "user")];

        return await this.GetChatCompletion(_client,
             options, cancellationToken: cancellationToken);
    }

    public virtual async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        options.Messages = [options.Messages.Last(a => a.Role == "user")];

        decimal? capturedCost = null;

        await foreach (var update in this.GetChatCompletions(_client,
            options,
            cancellationToken: cancellationToken))
        {
            if (TryCaptureUsageCost(update, out var cost))
            {
                capturedCost = cost;
                continue;
            }

            if (capturedCost is not null && TryApplyCapturedGatewayCost(update, capturedCost.Value))
                capturedCost = null;

            yield return update;
        }
    }

    public string GetIdentifier() => nameof(Brave).ToLowerInvariant();

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

    public static bool TryCaptureUsageCost(ChatCompletionUpdate update, out decimal cost)
    {
        cost = 0m;

        foreach (var content in EnumerateChoiceDeltaContent(update))
        {
            if (!TryExtractUsagePayload(content, out var usagePayload))
                continue;

            if (TryExtractTotalCost(usagePayload, out cost))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateChoiceDeltaContent(ChatCompletionUpdate update)
    {
        foreach (var choice in update.Choices ?? [])
        {
            if (choice is not JsonElement choiceElement || choiceElement.ValueKind != JsonValueKind.Object)
                continue;

            if (!choiceElement.TryGetProperty("delta", out var delta)
                || delta.ValueKind != JsonValueKind.Object
                || !delta.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = content.GetString();
            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }

    private static bool TryExtractUsagePayload(string content, out string payload)
    {
        payload = string.Empty;

        var start = content.IndexOf(UsageStartTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;

        start += UsageStartTag.Length;
        var end = content.IndexOf(UsageEndTag, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return false;

        payload = content[start..end];
        return !string.IsNullOrWhiteSpace(payload);
    }

    private static bool TryExtractTotalCost(string usagePayload, out decimal cost)
    {
        cost = 0m;

        try
        {
            using var document = JsonDocument.Parse(usagePayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryGetProperty(document.RootElement, "X-Request-Total-Cost", out var totalCost))
            {
                return false;
            }

            return TryGetDecimal(totalCost, out cost);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryApplyCapturedGatewayCost(ChatCompletionUpdate update, decimal cost)
    {
        var json = JsonSerializer.SerializeToNode(update, JsonSerializerOptions.Web) as JsonObject;
        if (json is null)
            return false;

        var metadata = json["metadata"] as JsonObject;
        if (metadata is null)
        {
            metadata = [];
            json["metadata"] = metadata;
        }

        if (HasGatewayCost(metadata))
            return true;

        metadata["gateway"] = new JsonObject
        {
            ["cost"] = cost
        };

        var enriched = json.Deserialize<ChatCompletionUpdate>(JsonSerializerOptions.Web);
        if (enriched is null)
            return false;

        update.AdditionalProperties = enriched.AdditionalProperties;
        return true;
    }

    private static bool HasGatewayCost(JsonObject metadata)
    {
        if (metadata["gateway"] is not JsonObject gateway
            || !gateway.TryGetPropertyValue("cost", out var costNode)
            || costNode is null)
        {
            return false;
        }

        var costElement = JsonSerializer.SerializeToElement(costNode, JsonSerializerOptions.Web);
        return TryGetDecimal(costElement, out _);
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetDecimal(JsonElement element, out decimal value)
    {
        value = 0m;

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var parsed) => (value = parsed) >= 0 || parsed < 0,
            JsonValueKind.String when decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => (value = parsed) >= 0 || parsed < 0,
            _ => false
        };
    }
}
