using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Jules;

public partial class JulesProvider
{
    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
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

                var models = new List<Model>();
                models.AddRange(GetIdentifier().GetModels());
                models.AddRange(await ListSourceShortcutModelsAsync(ct));

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

    private async Task<List<Model>> ListSourceShortcutModelsAsync(CancellationToken cancellationToken)
    {
        var models = new List<Model>();
        string? pageToken = null;

        do
        {
            var path = string.IsNullOrWhiteSpace(pageToken)
                ? "sources"
                : $"sources?pageToken={Uri.EscapeDataString(pageToken)}";

            using var response = await _client.GetAsync(path, cancellationToken);
            var json = await ReadJsonElementAsync(response, "Jules list sources", cancellationToken);

            if (json.TryGetProperty("sources", out var sourcesElement)
                && sourcesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sourcesElement.EnumerateArray())
                {
                    if (source.ValueKind != JsonValueKind.Object)
                        continue;

                    var sourceName = ExtractString(source, "name");
                    if (string.IsNullOrWhiteSpace(sourceName))
                        continue;

                    var owner = source.TryGetProperty("githubRepo", out var githubRepo)
                        ? ExtractString(githubRepo, "owner")
                        : null;
                    var repo = source.TryGetProperty("githubRepo", out githubRepo)
                        ? ExtractString(githubRepo, "repo")
                        : null;
                    var shortcutName = !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo)
                        ? $"{owner}/{repo}"
                        : ExtractString(source, "id") ?? sourceName;

                    var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "agent",
                        "shortcut",
                        "source",
                        $"source:{sourceName}"
                    };

                    if (!string.IsNullOrWhiteSpace(owner))
                        tags.Add($"owner:{owner}");
                    if (!string.IsNullOrWhiteSpace(repo))
                        tags.Add($"repo:{repo}");
                    if (source.TryGetProperty("githubRepo", out _))
                        tags.Add("github");

                    models.Add(new Model
                    {
                        Id = sourceName.ToModelId(GetIdentifier()),
                        Name = shortcutName,
                        OwnedBy = "Google",
                        Type = "language",
                        Description = $"Jules source shortcut for '{shortcutName}'. Use this model id to target source '{sourceName}'. Provide the branch via metadata.jules.startingBranch when creating a session.",
                        Tags = [.. tags]
                    });
                }
            }

            pageToken = ExtractString(json, "nextPageToken");
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return models;
    }
}
