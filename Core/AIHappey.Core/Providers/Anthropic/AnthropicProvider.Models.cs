using AIHappey.Core.Models;
using AIHappey.Core.AI;
using System.Globalization;
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
        var models = new List<Model>();
        var pricing = GetIdentifier().GetPricing();
        string? afterId = null;

        do
        {
            var uri = $"v1/models?limit=1000"
                + (afterId is null ? string.Empty : $"&after_id={Uri.EscapeDataString(afterId)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("x-api-key", GetKey());

            using var response = await _client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            foreach (var model in root.GetProperty("data").EnumerateArray())
            {
                var modelId = model.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(modelId))
                    continue;

                var modelIdentifier = modelId.ToModelId(GetIdentifier());
                var contextWindow = ContextSize.TryGetValue(modelId, out var contextSize)
                    ? contextSize
                    : (int?)null;
                var maxTokens = MaxOutput.TryGetValue(modelId, out var maxOutput)
                    ? maxOutput
                    : (int?)null;
                var modelPricing = pricing is not null && pricing.TryGetValue(modelIdentifier, out var value)
                    ? value
                    : null;
                var created = model.TryGetProperty("created_at", out var createdAt)
                    && createdAt.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(createdAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
                        ? timestamp.ToUnixTimeSeconds()
                        : (long?)null;

                models.Add(new Model
                {
                    Id = modelIdentifier,
                    Name = modelId,
                    ContextWindow = contextWindow,
                    MaxTokens = maxTokens,
                    Pricing = modelPricing,
                    Created = created,
                    OwnedBy = nameof(Anthropic),
                });
            }

            afterId = root.TryGetProperty("has_more", out var hasMore)
                && hasMore.ValueKind == JsonValueKind.True
                && root.TryGetProperty("last_id", out var lastId)
                && lastId.ValueKind == JsonValueKind.String
                ? lastId.GetString()
                : null;
        } while (!string.IsNullOrWhiteSpace(afterId));

        return models;
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

            var root = await SendManagedAgentsJsonAsync(HttpMethod.Get, uri, operation: "Anthropic managed agents list",
                cancellationToken: cancellationToken, captureDebug: false);
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

            var root = await SendManagedAgentsJsonAsync(HttpMethod.Get, uri, operation: "Anthropic managed agent environments list",
                cancellationToken: cancellationToken, captureDebug: false);
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
