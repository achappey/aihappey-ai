using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider : IModelProvider
{
    //    private static readonly JsonSerializerOptions MistralJsonSerializerOptions = JsonSerializerOptions.Web;

    private readonly HttpClient _client;

    private readonly IApiKeyResolver _keyResolver;

    private readonly AsyncCacheHelper _memoryCache;

    public MistralProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.mistral.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Mistral)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => nameof(Mistral).ToLowerInvariant();

    private string GetName() => nameof(Mistral);


    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
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