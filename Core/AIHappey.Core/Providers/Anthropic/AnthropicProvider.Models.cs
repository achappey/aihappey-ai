using AIHappey.Core.Models;
using AIHappey.Core.AI;
using ANT = Anthropic.SDK;
using System.Text.Json;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var standardModels = (await ListStandardModelsAsync(cancellationToken)).ToList();
        var managedAgentModels = (await ListManagedAgentModelsSafeAsync(cancellationToken)).ToList();

        return standardModels
            .Concat(managedAgentModels)
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(model => model.Created ?? 0);
    }

    private async Task<IEnumerable<Model>> ListStandardModelsAsync(CancellationToken cancellationToken)
    {
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

    private async Task<IEnumerable<Model>> ListManagedAgentModelsSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var agents = await ListManagedAgentsAsync(cancellationToken);
            var environments = await ListManagedAgentEnvironmentsAsync(cancellationToken);

            if (agents.Count == 0 || environments.Count == 0)
                return [];

            var models = new List<Model>();

            foreach (var agent in agents)
            {
                foreach (var environment in environments)
                {
                    var created = new[] { agent.CreatedAt, environment.CreatedAt }
                        .Where(static value => value.HasValue)
                        .Select(static value => value!.Value)
                        .DefaultIfEmpty(DateTimeOffset.UtcNow)
                        .Max();

                    AddManagedAgentModelIfMissing(models, new Model
                    {
                        Id = $"{ManagedAgentModelPrefix}{agent.Id}/{environment.Id}".ToModelId(GetIdentifier()),
                        Name = $"{GetManagedAgentDisplayName(agent)} @ {GetManagedAgentEnvironmentDisplayName(environment)}",
                        Description = BuildManagedAgentModelDescription(agent, environment),
                        OwnedBy = nameof(Anthropic),
                        Type = "language",
                        Created = created.ToUnixTimeSeconds(),
                        Tags = ["agent"]
                    });
                }
            }

            return models;
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<AnthropicManagedAgentDefinition>> ListManagedAgentsAsync(CancellationToken cancellationToken)
    {
        var results = new List<AnthropicManagedAgentDefinition>();
        string? page = null;

        while (true)
        {
            var uri = page is null
                ? $"{ManagedAgentsEndpoint}?limit={ManagedAgentListPageSize}"
                : $"{ManagedAgentsEndpoint}?limit={ManagedAgentListPageSize}&page={Uri.EscapeDataString(page)}";

            var root = await SendManagedAgentsJsonAsync(HttpMethod.Get, uri, operation: "Anthropic managed agents list", cancellationToken: cancellationToken);
            if (!TryGetProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            foreach (var item in data.EnumerateArray())
            {
                var id = TryGetString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                string? modelId = null;
                if (TryGetProperty(item, "model", out var model) && model.ValueKind == JsonValueKind.Object)
                    modelId = TryGetString(model, "id");

                results.Add(new AnthropicManagedAgentDefinition(
                    id,
                    TryGetString(item, "name"),
                    TryGetString(item, "description"),
                    modelId,
                    TryGetDateTimeOffset(item, "created_at")));
            }

            page = TryGetString(root, "next_page");
            if (string.IsNullOrWhiteSpace(page))
                break;
        }

        return results;
    }

    private async Task<IReadOnlyList<AnthropicManagedAgentEnvironmentDefinition>> ListManagedAgentEnvironmentsAsync(CancellationToken cancellationToken)
    {
        var results = new List<AnthropicManagedAgentEnvironmentDefinition>();
        string? page = null;

        while (true)
        {
            var uri = page is null
                ? $"{ManagedAgentEnvironmentsEndpoint}?limit={ManagedAgentListPageSize}"
                : $"{ManagedAgentEnvironmentsEndpoint}?limit={ManagedAgentListPageSize}&page={Uri.EscapeDataString(page)}";

            var root = await SendManagedAgentsJsonAsync(HttpMethod.Get, uri, operation: "Anthropic managed agent environments list", cancellationToken: cancellationToken);
            if (!TryGetProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            foreach (var item in data.EnumerateArray())
            {
                var id = TryGetString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                results.Add(new AnthropicManagedAgentEnvironmentDefinition(
                    id,
                    TryGetString(item, "name"),
                    TryGetString(item, "description"),
                    TryGetDateTimeOffset(item, "created_at")));
            }

            page = TryGetString(root, "next_page");
            if (string.IsNullOrWhiteSpace(page))
                break;
        }

        return results;
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
}
