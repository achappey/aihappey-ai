using AIHappey.Core.AI;
using AIHappey.Core.Models;
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
using AIHappey.Messages;

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

    public async Task<JsonElement> MessagesAsync(
      JsonElement request,
      Dictionary<string, string> headers,
      CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var options = request.Deserialize<MessagesRequest>()!;

        this.SetDefaultResponseProperties(options);

        return await _client.PostMessages(
            JsonSerializer.SerializeToElement(options),
            headers,
            ct: cancellationToken);
    }

    public IAsyncEnumerable<JsonElement> MessagesStreamingAsync(
        JsonElement request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var options = request.Deserialize<MessagesRequest>()!;

        this.SetDefaultResponseProperties(options);

        return _client.PostMessagesStreaming(
            JsonSerializer.SerializeToElement(options, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            }),
            headers,
            ct: cancellationToken);
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