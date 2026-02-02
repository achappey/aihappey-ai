using AIHappey.Core.AI;
using AIHappey.Core.Models;
using ANT = Anthropic.SDK;
using AIHappey.Core.ModelProviders;
using AIHappey.Common.Model;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Vercel.Models;

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
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var client = new ANT.AnthropicClient(GetKey());

        var models = await client.Models.ListModelsAsync(ctx: cancellationToken);

        return models.Models
            .Select(a => new Model()
            {
                Id = a.Id.ToModelId(GetIdentifier()),
                Name = a.Id,
                ContextWindow = ContextSize.TryGetValue(a.Id, out int s) ? s : null,
                MaxTokens = MaxOutput.TryGetValue(a.Id, out int m) ? m : null,
                Pricing = Prices.TryGetValue(a.Id, out ModelPricing? p) ? p : null,
                Created = new DateTimeOffset(a.CreatedAt.ToUniversalTime())
                        .ToUnixTimeSeconds(),
                OwnedBy = nameof(Anthropic),
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

    private readonly Dictionary<string, ModelPricing> Prices = new() {
        {"claude-sonnet-4-5-20250929", new ModelPricing()
        {
            Input = 3.00m,
            Output = 15.00m,
        }
        },
        {"claude-haiku-4-5-20251001", new ModelPricing()
        {
            Input = 1.00m,
            Output = 5.00m,
        }},
        {"claude-opus-4-5-20251101", new ModelPricing()
        {
            Input = 5.00m,
            Output = 25.00m,
        }}
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

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}