using AIHappey.Core.AI;
using AIHappey.Messages;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.ChatCompletions.Models;
using AIHappey.Vercel.Models;
using System.Text.Json;
using AIHappey.Core.Contracts;
using System.Globalization;
using AIHappey.Unified.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider : IModelProvider
{
    private readonly string BASE_URL = "https://api.perplexity.ai/";
    private const string AgentModelPrefix = "agent/";
    private const string RouterModelPrefix = "router/";

    public string GetIdentifier() => nameof(Perplexity).ToLowerInvariant();

    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public PerplexityProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
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

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetChatCompletion(_client,
             options,
             relativeUrl: "router/v1/chat/completions",
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetChatCompletions(_client,
                    options,
                    relativeUrl: "router/v1/chat/completions",
                    cancellationToken: cancellationToken);
    }


    private static bool UsesResponsesPreset(string? model)
        => string.Equals(model, "fast", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "low", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "medium", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "wide-research", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "high", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "xhigh", StringComparison.OrdinalIgnoreCase);


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
        throw new NotSupportedException();
    }

    public async Task<MessagesResponse> MessagesAsync(
       MessagesRequest request,
       Dictionary<string, string> headers,
       CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetMessage(_client,
            request,
            headers: headers,
            relativeUrl: "router/v1/messages",
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetMessages(_client,
            request,
            headers: headers,
            relativeUrl: "router/v1/messages",
            cancellationToken: cancellationToken);
    }


    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var (route, upstreamRequest) = PrepareUnifiedRequest(request);

        return route == PerplexityRoute.Agent
            ? this.ExecuteUnifiedViaResponsesAsync(upstreamRequest, cancellationToken: cancellationToken)
            : this.ExecuteUnifiedViaChatCompletionsAsync(upstreamRequest, cancellationToken: cancellationToken);
    }


    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var (route, upstreamRequest) = PrepareUnifiedRequest(request);

        return route == PerplexityRoute.Agent
            ? this.StreamUnifiedViaResponsesAsync(upstreamRequest, cancellationToken: cancellationToken)
            : this.StreamUnifiedViaChatCompletionsAsync(upstreamRequest, cancellationToken: cancellationToken);
    }

    private (PerplexityRoute Route, AIRequest Request) PrepareUnifiedRequest(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = request.Model?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException(
                $"A Perplexity unified model must use the '{AgentModelPrefix}' or '{RouterModelPrefix}' route prefix.",
                nameof(request));

        var providerPrefix = GetIdentifier() + "/";
        if (model.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            model = model[providerPrefix.Length..];

        PerplexityRoute route;
        string upstreamModel;

        if (model.StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            route = PerplexityRoute.Agent;
            upstreamModel = model[AgentModelPrefix.Length..].Trim();
        }
        else if (model.StartsWith(RouterModelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            route = PerplexityRoute.Router;
            upstreamModel = model[RouterModelPrefix.Length..].Trim();
        }
        else
        {
            throw new ArgumentException(
                $"Unsupported Perplexity unified model '{request.Model}'. Use '{AgentModelPrefix}<model>' or '{RouterModelPrefix}<model>'.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(upstreamModel))
            throw new ArgumentException("The Perplexity route prefix must be followed by an upstream model id.", nameof(request));

        return (route, CloneUnifiedRequest(request, upstreamModel));
    }

    private static AIRequest CloneUnifiedRequest(AIRequest request, string model)
        => new()
        {
            ProviderId = request.ProviderId,
            Model = model,
            Id = request.Id,
            Instructions = request.Instructions,
            Input = request.Input,
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxOutputTokens,
            MaxToolCalls = request.MaxToolCalls,
            Stream = request.Stream,
            ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice,
            ResponseFormat = request.ResponseFormat,
            Tools = request.Tools,
            Metadata = request.Metadata,
            Headers = request.Headers
        };

    private enum PerplexityRoute
    {
        Agent,
        Router
    }

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }



    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

}

