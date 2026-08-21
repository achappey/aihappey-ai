using System.Globalization;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Writer;

public partial class WriterProvider
{
    private const string AgentModelPrefix = "agent/";
    private const string KnowledgeGraphModelPrefix = "knowledge-graph/";

    private readonly Dictionary<string, WriterResourceDescriptor> _writerResources =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writerResourcesLock = new();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        var cacheKey = this.GetCacheKey(key);
        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();
                var models = new List<Model>();
                var resources = new Dictionary<string, WriterResourceDescriptor>(StringComparer.OrdinalIgnoreCase);

                await AddFoundationModelsAsync(models, ct);
                await AddApplicationModelsAsync(models, resources, ct);
                await AddKnowledgeGraphModelsAsync(models, resources, ct);

                // This generic model is useful when callers choose one or more graph_ids in
                // provider options rather than selecting a graph-specific catalog entry.
                models.Add(new Model
                {
                    Id = "knowledge-graph".ToModelId(GetIdentifier()),
                    Name = "Writer Knowledge Graph",
                    Description = "Query one or more Writer Knowledge Graphs selected with optional Writer provider options.",
                    OwnedBy = nameof(Writer),
                    Type = "language",
                    Tags = ["knowledge-graph", "rag"]
                });

                lock (_writerResourcesLock)
                {
                    _writerResources.Clear();
                    foreach (var item in resources)
                        _writerResources[item.Key] = item.Value;
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task AddFoundationModelsAsync(List<Model> models, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("v1/models", cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateWriterApiExceptionAsync(response, "list models", cancellationToken);

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("models", out var data) || data.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in data.EnumerateArray())
        {
            var id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;
            models.Add(new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = ReadString(item, "name") ?? id,
                OwnedBy = nameof(Writer),
                Type = "language"
            });
        }
    }

    private async Task AddApplicationModelsAsync(
        List<Model> models,
        Dictionary<string, WriterResourceDescriptor> resources,
        CancellationToken cancellationToken)
    {
        var applications = await GetAllWriterApplicationsAsync(cancellationToken);
        var slugs = CreateUniqueSlugs(applications.Select(item => (item.Id, item.Name)));
        foreach (var application in applications)
        {
            var slug = slugs[application.Id];
            var descriptor = new WriterResourceDescriptor("agent", application.Id, slug, application.Name, application);
            RegisterResource(resources, descriptor);
            models.Add(new Model
            {
                Id = (AgentModelPrefix + slug).ToModelId(GetIdentifier()),
                Name = application.Name,
                Description = $"Writer no-code agent. Resource ID: {application.Id}.",
                Created = ToUnixSeconds(application.CreatedAt),
                OwnedBy = nameof(Writer),
                Type = "language",
                Tags = ["agent", "async"]
            });
        }
    }

    private async Task AddKnowledgeGraphModelsAsync(
        List<Model> models,
        Dictionary<string, WriterResourceDescriptor> resources,
        CancellationToken cancellationToken)
    {
        var graphs = await GetAllWriterGraphsAsync(cancellationToken);
        var slugs = CreateUniqueSlugs(graphs.Select(item => (item.Id, item.Name)));
        foreach (var graph in graphs)
        {
            var slug = slugs[graph.Id];
            var descriptor = new WriterResourceDescriptor("knowledge-graph", graph.Id, slug, graph.Name, Graph: graph);
            RegisterResource(resources, descriptor);
            models.Add(new Model
            {
                Id = (KnowledgeGraphModelPrefix + slug).ToModelId(GetIdentifier()),
                Name = graph.Name,
                Description = graph.Description ?? $"Writer Knowledge Graph. Resource ID: {graph.Id}.",
                Created = ToUnixSeconds(graph.CreatedAt),
                OwnedBy = nameof(Writer),
                Type = "language",
                Tags = ["knowledge-graph", "rag", graph.Type ?? "unknown"]
            });
        }
    }

    private static void RegisterResource(
        Dictionary<string, WriterResourceDescriptor> resources,
        WriterResourceDescriptor descriptor)
    {
        resources[$"{descriptor.Kind}/{descriptor.Slug}"] = descriptor;
        resources[$"{descriptor.Kind}/{descriptor.Id}"] = descriptor;
    }

    private async Task<WriterResourceDescriptor?> ResolveWriterResourceAsync(
        string? model,
        CancellationToken cancellationToken)
    {
        var local = NormalizeWriterModel(model);
        if (!local.StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase)
            && !local.StartsWith(KnowledgeGraphModelPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        lock (_writerResourcesLock)
            if (_writerResources.TryGetValue(local, out var cached))
                return cached;

        await ListModels(cancellationToken);
        lock (_writerResourcesLock)
            if (_writerResources.TryGetValue(local, out var listed))
                return listed;

        var slash = local.IndexOf('/');
        var kind = local[..slash];
        var target = local[(slash + 1)..];
        return Guid.TryParse(target, out _)
            ? new WriterResourceDescriptor(kind, target, target, target)
            : null;
    }

    private static bool IsWriterAgentModel(string? model)
        => NormalizeWriterModel(model).StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsWriterKnowledgeGraphModel(string? model)
    {
        var local = NormalizeWriterModel(model);
        return local.Equals("knowledge-graph", StringComparison.OrdinalIgnoreCase)
               || local.StartsWith(KnowledgeGraphModelPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWriterModel(string? model)
    {
        var value = model?.Trim() ?? string.Empty;
        return value.StartsWith("writer/", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
    }

    private static Dictionary<string, string> CreateUniqueSlugs(IEnumerable<(string Id, string Name)> resources)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in resources.GroupBy(item => Slugify(item.Name), StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < entries.Length; index++)
            {
                var suffix = entries.Length == 1 ? string.Empty : $"-{entries[index].Id[..Math.Min(8, entries[index].Id.Length)]}";
                result[entries[index].Id] = group.Key + suffix;
            }
        }
        return result;
    }

    private static string Slugify(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var separator = false;
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                separator = false;
            }
            else if (!separator && builder.Length > 0)
            {
                builder.Append('-');
                separator = true;
            }
        }
        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "resource" : slug;
    }

    private static long? ToUnixSeconds(DateTimeOffset? value) => value?.ToUnixTimeSeconds();
}
