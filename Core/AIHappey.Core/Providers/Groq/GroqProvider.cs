using AIHappey.Common.Model.ChatCompletions;
using System.Net.Http.Headers;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Common.Model;
using AIHappey.Responses;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Responses.Extensions;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider : IModelProvider
{
    private readonly HttpClient _client;

    private readonly IApiKeyResolver _keyResolver;

    private readonly AsyncCacheHelper _memoryCache;

    public GroqProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.groq.com/openai/");
        _client.DefaultRequestHeaders.Add("Groq-Model-Version", "latest");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Groq)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => GroqExtensions.Identifier();


    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await _client.GetChatCompletion(
             options, ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.GetChatCompletionUpdates(
                    options, ct: cancellationToken);
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        this.SetDefaultResponseProperties(options);

        var response = await _client.GetResponses(
                   options, ct: cancellationToken);

        var pricing = ResolveCatalogPricing(string.IsNullOrWhiteSpace(response.Model)
            ? options.Model
            : response.Model);

        response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(
            response.Usage,
            response.Metadata,
            pricing);

        return response;
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        this.SetDefaultResponseProperties(options);

        return ResponsesStreamingInternalAsync(options, cancellationToken);
    }

    private async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingInternalAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _client.GetResponsesUpdates(
                           options,
                           ct: cancellationToken))
        {
            if (update is Responses.Streaming.ResponseCompleted completed)
            {
                var pricing = ResolveCatalogPricing(string.IsNullOrWhiteSpace(completed.Response.Model)
                    ? options.Model
                    : completed.Response.Model);

                completed.Response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(
                    completed.Response.Usage,
                    completed.Response.Metadata,
                    pricing);
            }

            yield return update;
        }
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
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

    private ModelPricing? ResolveCatalogPricing(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var pricing = GetIdentifier().GetPricing();
        if (pricing == null || pricing.Count == 0)
            return null;

        var candidates = new[]
        {
            modelId,
            modelId.ToModelId(GetIdentifier()),
            modelId.StartsWith($"{GetIdentifier()}/", StringComparison.OrdinalIgnoreCase)
                ? modelId[(GetIdentifier().Length + 1)..]
                : null
        }
        .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (candidate != null && pricing.TryGetValue(candidate, out var modelPricing))
                return modelPricing;
        }

        return null;
    }
}
