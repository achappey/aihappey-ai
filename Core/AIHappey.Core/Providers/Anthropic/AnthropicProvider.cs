using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Messages;
using ANT = Anthropic.SDK;
using AIHappey.Common.Model;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Responses.Mapping;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using System.Text.Json;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Messages.Mapping;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public string GetIdentifier() => AnthropicConstants.AnthropicIdentifier;

    private string GetKey()
    {
        var key = _keyResolver.Resolve(AnthropicConstants.AnthropicIdentifier);

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Anthropic)} API key.");

        return key;
    }

    public void AddBetaHeaders(IEnumerable<string>? headers)
    {
        _client.DefaultRequestHeaders.Remove("anthropic-beta");

        if (headers?.Any() == true)
            _client.DefaultRequestHeaders.Add("anthropic-beta", string.Join(',', headers));
    }

    public AnthropicProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _client.BaseAddress = new Uri("https://api.anthropic.com/");
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var client = new ANT.AnthropicClient(GetKey());

        var models = await client.Models.ListModelsAsync(ctx: cancellationToken);
        var pricing = GetIdentifier().GetPricing();

        return models.Models.Select(a =>
        {
            var modelId = a.Id.ToModelId(GetIdentifier());

            var contextWindow =
                ContextSize.TryGetValue(a.Id, out int value)
                    ? value : (int?)null;

            var maxTokens =
                MaxOutput.TryGetValue(a.Id, out int maxTokensValue)
                    ? maxTokensValue : (int?)null;

            var modelPricing =
                pricing != null && pricing.ContainsKey(modelId)
                    ? pricing[modelId]
                    : null;

            return new Model
            {
                Id = modelId,
                Name = a.Id,
                ContextWindow = contextWindow,
                MaxTokens = maxTokens,
                Pricing = modelPricing,
                Created = new DateTimeOffset(a.CreatedAt.ToUniversalTime())
                    .ToUnixTimeSeconds(),
                OwnedBy = nameof(Anthropic),
            };
        });

    }

    private readonly Dictionary<string, int> ContextSize = new() {
        {"claude-sonnet-4-5-20250929", 200_000},
        {"claude-haiku-4-5-20251001", 200_000},
        {"claude-opus-4-5-20251101", 200_000}
      };

    private readonly Dictionary<string, int> MaxOutput = new() {
        {"claude-sonnet-4-5-20250929", 64_000},
        {"claude-haiku-4-5-20251001", 64_000},
        {"claude-opus-4-5-20251101", 64_000}
      };

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }


    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var result = await this.ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToResponseResult();
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
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
    {
        throw new NotImplementedException();
    }

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    private readonly string betaKey = "anthropic-beta";

    public async Task<MessagesResponse> MessagesAsync(
      MessagesRequest request,
      Dictionary<string, string> headers,
      CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var anthropicbeta = request.Metadata?.GetProviderOption<string?>(GetIdentifier(), betaKey);
        headers.AppendOrAddHeader(betaKey, anthropicbeta);

        this.SetDefaultMessagesProperties(request);

        var response = await this.GetMessage(_client,
            request,
            headers: headers,
            cancellationToken: cancellationToken);

        var pricing = ResolveModelPricing(response?.Model);

        return EnrichMessagesResponseJson(response!, response?.Usage, pricing);
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest options,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var anthropicbeta = options.Metadata?.GetProviderOption<string?>(GetIdentifier(), betaKey);
        headers.AppendOrAddHeader(betaKey, anthropicbeta);

        this.SetDefaultMessagesProperties(options);

        MessagesUsage? usage = null;
        string? responseModel = null;

        await foreach (var part in this.GetMessages(_client,
            options,
            headers: headers,
            cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(part?.Message?.Model))
                responseModel = part.Message.Model;

            if (part?.Message?.Usage is not null)
                usage = MergeUsage(usage, part.Message.Usage);

            if (part?.Usage is not null)
                usage = MergeUsage(usage, part.Usage);

            if (string.Equals(part?.Type, "message_stop", StringComparison.OrdinalIgnoreCase))
            {
                var pricing = ResolveModelPricing(responseModel);
                yield return EnrichMessageStreamPartJson(part!, usage, pricing);
                continue;
            }

            yield return part!;
        }
    }

    private ModelPricing? ResolveModelPricing(string? modelId)
    {
        var pricing = GetIdentifier().GetPricing();
        if (pricing is null || pricing.Count == 0)
            return null;
      
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        return pricing.TryGetValue(modelId, out var modelPricing)
            ? modelPricing
            : null;
    }

    private static MessagesResponse EnrichMessagesResponseJson(
        MessagesResponse response,
        MessagesUsage? usage,
        ModelPricing? pricing)
    {
        if (pricing is null || usage is null)
            return response;

        response.Metadata = ModelCostMetadataEnricher.AddCost(response.Metadata, ComputeMessagesCost(usage, pricing));
        return response;
    }

    private static MessageStreamPart EnrichMessageStreamPartJson(
        MessageStreamPart part,
        MessagesUsage? usage,
        ModelPricing? pricing)
    {
        if (pricing is null || usage is null)
            return part;

        part.Metadata = ModelCostMetadataEnricher.AddCost(part.Metadata, ComputeMessagesCost(usage, pricing));
        return part;
    }

    private static decimal? ComputeMessagesCost(
        MessagesUsage? usage,
        ModelPricing? pricing)
    {
        if (usage is null || pricing is null)
            return null;

        var inputTokens = usage.InputTokens ?? 0;
        var outputTokens = usage.OutputTokens ?? 0;
        var cachedInputReadTokens = usage.CacheReadInputTokens ?? 0;
        var cachedInputWriteTokens = GetCacheCreationInputTokens(usage.CacheCreation);

        if (inputTokens <= 0 && outputTokens <= 0 && cachedInputReadTokens <= 0 && cachedInputWriteTokens <= 0)
            return null;

        return ModelCostMetadataEnricher.ComputeCost(
            pricing,
            inputTokens,
            outputTokens,
            cachedInputReadTokens,
            cachedInputWriteTokens);
    }

    private static int GetCacheCreationInputTokens(MessagesCacheCreation? cacheCreation)
        => (cacheCreation?.Ephemeral1hInputTokens ?? 0)
            + (cacheCreation?.Ephemeral5mInputTokens ?? 0);

    private static MessagesUsage? MergeUsage(MessagesUsage? current, MessagesUsage? update)
    {
        if (update is null)
            return current;

        if (current is null)
            return update;

        return new MessagesUsage
        {
            CacheCreation = MergeCacheCreation(current.CacheCreation, update.CacheCreation),
            CacheCreationInputTokens = update.CacheCreationInputTokens ?? current.CacheCreationInputTokens,
            CacheReadInputTokens = update.CacheReadInputTokens ?? current.CacheReadInputTokens,
            InferenceGeo = update.InferenceGeo ?? current.InferenceGeo,
            InputTokens = update.InputTokens ?? current.InputTokens,
            OutputTokens = update.OutputTokens ?? current.OutputTokens,
            ServerToolUse = update.ServerToolUse ?? current.ServerToolUse,
            ServiceTier = update.ServiceTier ?? current.ServiceTier,
            AdditionalProperties = MergeAdditionalProperties(current.AdditionalProperties, update.AdditionalProperties)
        };
    }

    private static MessagesCacheCreation? MergeCacheCreation(MessagesCacheCreation? current, MessagesCacheCreation? update)
    {
        if (update is null)
            return current;

        if (current is null)
            return update;

        return new MessagesCacheCreation
        {
            Ephemeral1hInputTokens = update.Ephemeral1hInputTokens ?? current.Ephemeral1hInputTokens,
            Ephemeral5mInputTokens = update.Ephemeral5mInputTokens ?? current.Ephemeral5mInputTokens
        };
    }

    private static Dictionary<string, JsonElement>? MergeAdditionalProperties(
        Dictionary<string, JsonElement>? current,
        Dictionary<string, JsonElement>? update)
    {
        if (current is null || current.Count == 0)
            return update;

        if (update is null || update.Count == 0)
            return current;

        var merged = new Dictionary<string, JsonElement>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var item in update)
            merged[item.Key] = item.Value;

        return merged;
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Anthropic)} API key.");

        _client.DefaultRequestHeaders.Remove("X-API-Key");
        _client.DefaultRequestHeaders.Add("X-API-Key", key);
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
       => this.ExecuteUnifiedViaMessagesAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaMessagesAsync(request, cancellationToken: cancellationToken);

}

