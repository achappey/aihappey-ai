using AIHappey.Core.AI;
using AIHappey.Core.Models;
using ANT = Anthropic.SDK;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private static readonly string[] BetaFeatures =
        [
            "code-execution-2025-08-25",
            "files-api-2025-04-14",
            "output-128k-2025-02-19",
            "interleaved-thinking-2025-05-14",
            "web-fetch-2025-09-10",
            "context-management-2025-06-27",
            "fine-grained-tool-streaming-2025-05-14",
            "mcp-client-2025-04-04",
            "skills-2025-10-02"
        ];

    public string GetIdentifier() => AnthropicConstants.AnthropicIdentifier;

    private string GetKey()
    {
        var key = _keyResolver.Resolve(AnthropicConstants.AnthropicIdentifier);

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Anthropic)} API key.");

        return key;
    }

    public AnthropicProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.DefaultRequestHeaders.Add("anthropic-beta", string.Join(',', BetaFeatures));
        _client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var client = new ANT.AnthropicClient(
          GetKey()
        );

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

}