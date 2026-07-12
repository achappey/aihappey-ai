using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text.Json;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;
using System.Text;
using System.Net.Mime;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory, AsyncCacheHelper _memoryCache)
    : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api.deepinfra.com/");
        return client;
    }

    public string GetIdentifier() => nameof(DeepInfra).ToLowerInvariant();

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No DeepInfra API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var response = await this.GetChatCompletion(_client,
             options,
             relativeUrl: "v1/openai/chat/completions",
             cancellationToken: cancellationToken);

        return EnrichChatCompletionWithGatewayCost(response);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in this.GetChatCompletions(_client,
                    options,
                    relativeUrl: "v1/openai/chat/completions",
                    cancellationToken: cancellationToken))
        {
            yield return EnrichChatCompletionUpdateWithGatewayCost(update);
        }
    }

    public Task<CreateMessageResult> SamplingAsync(
        CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(options.Model, cancellationToken);

        if (model?.Type == "speech")
            return await this.SpeechResponseAsync(options, cancellationToken: cancellationToken);

        return (await ExecuteUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
            .ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
        {
            yield return part.ToResponseStreamPart();
        }
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public async Task<MessagesResponse> MessagesAsync(
       MessagesRequest request,
       Dictionary<string, string> headers,
       CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var response = await this.GetMessage(_client,
            request,
            "anthropic/v1/messages",
            headers: headers,
            cancellationToken: cancellationToken);

        return response;
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var part in this.GetMessages(_client,
            request,
            "anthropic/v1/messages",
            headers: headers,
            cancellationToken: cancellationToken))
        {
            yield return part;
        }
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var response = await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
        return new AIResponse
        {
            ProviderId = response.ProviderId,
            Model = response.Model,
            Status = response.Status,
            Output = response.Output,
            Usage = response.Usage,
            Metadata = ModelCostMetadataEnricher.AddCost(response.Metadata, GetGatewayCost(response.Usage))
        };
    }

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    private static ChatCompletion EnrichChatCompletionWithGatewayCost(ChatCompletion response)
    {
        var cost = GetGatewayCost(response.Usage);

        response.Usage = UpsertUsageCost(response.Usage, cost);
        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            response.AdditionalProperties,
            cost);

        return response;
    }

    private static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCost(ChatCompletionUpdate update)
    {
        var cost = GetGatewayCost(update.Usage);

        update.Usage = UpsertUsageCost(update.Usage, cost);
        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            update.AdditionalProperties,
            cost);

        return update;
    }

    public static ChatCompletion EnrichChatCompletionWithGatewayCostForTests(ChatCompletion response)
        => EnrichChatCompletionWithGatewayCost(response);

    public static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCostForTests(ChatCompletionUpdate update)
        => EnrichChatCompletionUpdateWithGatewayCost(update);

    public static decimal? GetGatewayCost(object? usage)
    {
        if (usage is null)
            return null;

        try
        {
            var usageElement = usage switch
            {
                JsonElement json => json,
                _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
            };

            if (usageElement.ValueKind != JsonValueKind.Object
                || !TryGetDeepInfraProperty(usageElement, "estimated_cost", out var costElement))
            {
                return null;
            }

            return TryGetDeepInfraDecimal(costElement);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, JsonElement>? AddGatewayCostToChatCompletionMetadata(
        Dictionary<string, JsonElement>? additionalProperties,
        decimal? cost)
    {
        if (!cost.HasValue)
            return additionalProperties;

        var enrichedAdditionalProperties = additionalProperties is not null
            ? new Dictionary<string, JsonElement>(additionalProperties, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, JsonElement>? existingMetadata = null;
        if (additionalProperties is not null
            && additionalProperties.TryGetValue("metadata", out var metadataElement)
            && metadataElement.ValueKind == JsonValueKind.Object)
        {
            existingMetadata = metadataElement.Deserialize<Dictionary<string, JsonElement>>(JsonSerializerOptions.Web);
        }

        enrichedAdditionalProperties["metadata"] = JsonSerializer.SerializeToElement(
            ModelCostMetadataEnricher.AddCost(existingMetadata, cost),
            JsonSerializerOptions.Web);

        return enrichedAdditionalProperties;
    }

    private static object? UpsertUsageCost(object? usage, decimal? cost)
    {
        if (!cost.HasValue || usage is null)
            return usage;

        var usageElement = usage switch
        {
            JsonElement json => json,
            _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
        };

        if (usageElement.ValueKind != JsonValueKind.Object)
            return usage;

        var usageData = usageElement.Deserialize<Dictionary<string, JsonElement>>(JsonSerializerOptions.Web)
            ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        usageData["cost"] = JsonSerializer.SerializeToElement(cost.Value, JsonSerializerOptions.Web);

        return JsonSerializer.SerializeToElement(usageData, JsonSerializerOptions.Web);
    }

    private static decimal? TryGetDeepInfraDecimal(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var parsed) => parsed,
            JsonValueKind.String when decimal.TryParse(
                element.GetString(),
                NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };

    private static bool TryGetDeepInfraProperty(JsonElement element, string propertyName, out JsonElement value)
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

    private async Task<ImageResponse> ImageGenerateAsync(
        ImageRequest req,
        CancellationToken ct)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = req.Prompt,
            ["num_results"] = req.N ?? 1,
        };

        if (!string.IsNullOrEmpty(req.AspectRatio))
        {
            payload["aspect_ratio"] = req.AspectRatio;
        }

        if (req.Seed is not null)
        {
            payload["seed"] = req.Seed;
        }

        var json = JsonSerializer.Serialize(payload, ImageJson);

        using var resp = await _client.PostAsync(
            $"v1/inference/{req.Model}",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            ct);

        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var images = await ExtractImagesAsDataUrlsAsync(raw, ct);
        if (images.Count == 0)
            throw new Exception("DeepInfra returned no images.");

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = req.Model.ToModelId(GetIdentifier())
            }
        };
    }

}

