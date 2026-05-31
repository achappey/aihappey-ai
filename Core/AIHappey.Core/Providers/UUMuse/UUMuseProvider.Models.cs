using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.UUMuse;

public partial class UUMuseProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var nativeModels = await ListUUMuseNativeModelsAsync(cancellationToken);
                var workspaces = await ListUUMuseWorkspacesAsync(cancellationToken);

                var models = nativeModels.Select(ToModel).ToList();
                foreach (var workspace in workspaces)
                    models.AddRange(nativeModels.Select(nativeModel => ToWorkspaceShortcutModel(nativeModel, workspace)));

                return models
                    .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                    .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<List<UUMuseNativeModel>> ListUUMuseNativeModelsAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"UUMuse models API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        var arr = root.TryGetProperty("models", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
            ? dataEl.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

        var models = new List<UUMuseNativeModel>();
        foreach (var el in arr)
        {
            var id = GetString(el, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            models.Add(new UUMuseNativeModel
            {
                Id = id!,
                Name = GetString(el, "name") ?? id!,
                Provider = GetString(el, "provider"),
                Tier = GetString(el, "tier"),
                ContextWindow = GetInt(el, "context_window"),
                MaxOutputTokens = GetInt(el, "max_output_tokens")
            });
        }

        return models;
    }

    private async Task<List<UUMuseWorkspace>> ListUUMuseWorkspacesAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/workspaces");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"UUMuse workspaces API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        var arr = root.TryGetProperty("workspaces", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
            ? dataEl.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

        var workspaces = new List<UUMuseWorkspace>();
        foreach (var el in arr)
        {
            var id = GetString(el, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            workspaces.Add(new UUMuseWorkspace
            {
                Id = id!,
                Name = GetString(el, "name") ?? id!,
                Description = GetString(el, "description"),
                FileCount = GetInt(el, "file_count"),
                CreatedAt = GetString(el, "created_at")
            });
        }

        return workspaces;
    }

    private Model ToModel(UUMuseNativeModel nativeModel)
        => new()
        {
            Id = nativeModel.Id.ToModelId(GetIdentifier()),
            Name = nativeModel.Name,
            OwnedBy = nativeModel.Provider ?? "UUMuse",
            Type = "language",
            ContextWindow = nativeModel.ContextWindow,
            MaxTokens = nativeModel.MaxOutputTokens,
            Description = BuildModelDescription(nativeModel),
            Tags = BuildNativeModelTags(nativeModel)
        };

    private Model ToWorkspaceShortcutModel(UUMuseNativeModel nativeModel, UUMuseWorkspace workspace)
        => new()
        {
            Id = $"{nativeModel.Id}@{workspace.Id}".ToModelId(GetIdentifier()),
            Name = $"{nativeModel.Name} @ {workspace.Name}",
            OwnedBy = nativeModel.Provider ?? "UUMuse",
            Type = "language",
            ContextWindow = nativeModel.ContextWindow,
            MaxTokens = nativeModel.MaxOutputTokens,
            Description = $"UUMuse workspace shortcut for model '{nativeModel.Id}' and workspace '{workspace.Name}' ({workspace.Id}).",
            Tags = BuildWorkspaceShortcutTags(nativeModel, workspace)
        };

    private static string BuildModelDescription(UUMuseNativeModel nativeModel)
    {
        var parts = new List<string> { "UUMuse RAG ask model." };
        if (!string.IsNullOrWhiteSpace(nativeModel.Tier))
            parts.Add($"Tier: {nativeModel.Tier}.");
        parts.Add("Requires metadata.uumuse.workspace_id unless using a model@workspace shortcut.");
        return string.Join(" ", parts);
    }

    private static List<string> BuildNativeModelTags(UUMuseNativeModel nativeModel)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rag",
            "ask",
            "knowledge",
            "workspace-required"
        };

        if (!string.IsNullOrWhiteSpace(nativeModel.Provider))
            tags.Add($"provider:{nativeModel.Provider}");
        if (!string.IsNullOrWhiteSpace(nativeModel.Tier))
            tags.Add($"tier:{nativeModel.Tier}");

        return [.. tags];
    }

    private static List<string> BuildWorkspaceShortcutTags(UUMuseNativeModel nativeModel, UUMuseWorkspace workspace)
    {
        var tags = new HashSet<string>(BuildNativeModelTags(nativeModel), StringComparer.OrdinalIgnoreCase)
        {
            "shortcut",
            "workspace",
            $"workspace:{workspace.Id}"
        };

        if (!string.IsNullOrWhiteSpace(workspace.Name))
            tags.Add($"workspace-name:{workspace.Name}");

        return [.. tags];
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => property.ToString()
        };
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private sealed class UUMuseNativeModel
    {
        public string Id { get; init; } = default!;

        public string Name { get; init; } = default!;

        public string? Provider { get; init; }

        public string? Tier { get; init; }

        public int? ContextWindow { get; init; }

        public int? MaxOutputTokens { get; init; }
    }

    private sealed class UUMuseWorkspace
    {
        public string Id { get; init; } = default!;

        public string Name { get; init; } = default!;

        public string? Description { get; init; }

        public int? FileCount { get; init; }

        public string? CreatedAt { get; init; }
    }
}
