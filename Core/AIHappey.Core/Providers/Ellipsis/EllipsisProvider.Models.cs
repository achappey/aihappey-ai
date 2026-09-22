using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Ellipsis;

public partial class EllipsisProvider
{
    private const string EllipsisAgentsEndpoint = "v1/agents";
    private const string EllipsisAccountModelsEndpoint = "v1/account/models";
    private const string EllipsisAgentModelPrefix = "agent/";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                var models = GetIdentifier().GetModels().ToList();

                await AddEllipsisHarnessModelsAsync(models, ct);
                await AddEllipsisAgentModelsAsync(models, ct);

                return (IEnumerable<Model>)models
                    .GroupBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.Last())
                    .OrderBy(static model => model.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            },
            baseTtl: TimeSpan.FromMinutes(15),
            jitterMinutes: 2,
            cancellationToken: cancellationToken);
    }

    private async Task AddEllipsisHarnessModelsAsync(List<Model> result, CancellationToken cancellationToken)
    {
        try
        {
            var root = await SendEllipsisJsonAsync(
                HttpMethod.Get,
                EllipsisAccountModelsEndpoint,
                operation: "Ellipsis list account models",
                cancellationToken: cancellationToken);

            if (!TryGetEllipsisProperty(root, "models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in models.EnumerateArray())
            {
                var id = GetEllipsisString(entry, "id");
                var harness = NormalizeEllipsisHarness(GetEllipsisString(entry, "harness"));
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(harness))
                    continue;

                var isDefault = GetEllipsisBoolean(entry, "is_default_agent_model") == true;
                var displayName = GetEllipsisString(entry, "display_name") ?? id;
                var manufacturer = GetEllipsisString(entry, "manufacturer");
                var localId = isDefault ? harness : $"{harness}/{id}";

                result.Add(new Model
                {
                    Id = localId.ToModelId(GetIdentifier()),
                    Name = isDefault
                        ? EllipsisHarnessDisplayName(harness)
                        : $"{EllipsisHarnessDisplayName(harness)} · {displayName}",
                    Description = isDefault
                        ? $"Ellipsis {EllipsisHarnessDisplayName(harness)} agent using its default model."
                        : $"Ellipsis {EllipsisHarnessDisplayName(harness)} agent using {displayName}.",
                    OwnedBy = string.IsNullOrWhiteSpace(manufacturer)
                        ? nameof(Ellipsis)
                        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(manufacturer),
                    Type = "language",
                    Tags = isDefault
                        ? ["agent", "harness", "default"]
                        : ["agent", "harness"]
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The static claude_code and codex aliases remain available when account discovery is unavailable.
        }
    }

    private async Task AddEllipsisAgentModelsAsync(List<Model> result, CancellationToken cancellationToken)
    {
        try
        {
            var root = await SendEllipsisJsonAsync(
                HttpMethod.Get,
                EllipsisAgentsEndpoint,
                operation: "Ellipsis list agents",
                cancellationToken: cancellationToken);

            if (!TryGetEllipsisProperty(root, "agents", out var agents)
                || agents.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var agent in agents.EnumerateArray())
            {
                var id = GetEllipsisString(agent, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var name = GetEllipsisAgentName(agent) ?? id;
                var description = GetEllipsisAgentDescription(agent);
                var enabled = GetEllipsisNestedBoolean(agent, "config", "ellipsis", "enabled");
                if (enabled == false)
                    continue;

                result.Add(new Model
                {
                    Id = $"{EllipsisAgentModelPrefix}{id}".ToModelId(GetIdentifier()),
                    Name = name,
                    Description = string.IsNullOrWhiteSpace(description)
                        ? $"Ellipsis saved agent '{name}'."
                        : description,
                    OwnedBy = nameof(Ellipsis),
                    Type = "language",
                    Created = GetEllipsisDateTimeOffset(agent, "created_at")?.ToUnixTimeSeconds(),
                    Tags = ["agent", "saved-agent"]
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Saved-agent discovery is additive and must not hide the built-in harness aliases.
        }
    }

    private static string? GetEllipsisAgentName(JsonElement agent)
        => GetEllipsisString(agent, "name")
           ?? GetEllipsisNestedString(agent, "config", "ellipsis", "name")
           ?? GetEllipsisNestedString(agent, "ellipsis", "name");

    private static string? GetEllipsisAgentDescription(JsonElement agent)
        => GetEllipsisString(agent, "description")
           ?? GetEllipsisNestedString(agent, "config", "ellipsis", "description")
           ?? GetEllipsisNestedString(agent, "ellipsis", "description");

    private static string NormalizeEllipsisHarness(string? harness)
        => harness?.Trim().ToLowerInvariant().Replace('-', '_') ?? string.Empty;

    private static string EllipsisHarnessDisplayName(string harness)
        => string.Equals(harness, "claude_code", StringComparison.OrdinalIgnoreCase)
            ? "Claude Code"
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(harness.Replace('_', ' '));
}
