using AIHappey.Core.AI;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.ChatCompletions.Models;
using AIHappey.Vercel.Models;
using System.Text.Json;
using AIHappey.Core.Contracts;
using System.Globalization;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider : IModelProvider
{
    private readonly string BASE_URL = "https://api.perplexity.ai/";

    public string GetIdentifier() => nameof(Perplexity).ToLowerInvariant();

    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public PerplexityProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri(BASE_URL);
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Perplexity)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = chatRequest.GetModel();
        if (model?.StartsWith($"sonar") != true)
        {
            return await this.ResponsesSamplingAsync(chatRequest, cancellationToken);
        }

        ApplyAuthHeader();

        IEnumerable<Models.PerplexityMessage> inputItems = chatRequest.Messages.ToPerplexityMessages();
        var req = chatRequest.ToChatRequest(inputItems, chatRequest.SystemPrompt);

        var result = await _client.ChatCompletion(req, cancellationToken);
        var sources = result?.SearchResults?.DistinctBy(a => a.Url) ?? [];
        var mainText = result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        var finished = result?.Choices?.FirstOrDefault()?.FinishReason ?? string.Empty;

        var resourceLinks = sources.Select(a => new ResourceLinkBlock()
        {
            Uri = a.Url,
            Name = a.Title,
            Description = a.Snippet
        });

        ContentBlock contentBlock = mainText.ToTextContentBlock();

        return new CreateMessageResult()
        {
            Model = result?.Model!,
            StopReason = finished,
            Content = [contentBlock, .. resourceLinks],
            Role = ModelContextProtocol.Protocol.Role.Assistant,
            Meta = new System.Text.Json.Nodes.JsonObject()
            {
                ["inputTokens"] = result?.Usage?.PromptTokens,
                ["outputTokens"] = result?.Usage?.CompletionTokens,
                ["totalTokens"] = result?.Usage?.TotalTokens
            }
        };
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
            => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }


    private static bool UsesResponsesPreset(string? model)
        => string.Equals(model, "fast-search", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "pro-search", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "deep-research", StringComparison.OrdinalIgnoreCase);


    private static decimal? TryGetPerplexityTotalCost(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetProperty(usage, "cost", out var costElement) || costElement.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetProperty(costElement, "total_cost", out var totalCostElement))
            return null;

        return totalCostElement.ValueKind switch
        {
            JsonValueKind.Number when totalCostElement.TryGetDecimal(out var totalCost) => totalCost,
            JsonValueKind.String when decimal.TryParse(totalCostElement.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString();
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



    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<JsonElement> MessagesAsync(JsonElement request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<JsonElement> MessagesStreamingAsync(JsonElement request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

