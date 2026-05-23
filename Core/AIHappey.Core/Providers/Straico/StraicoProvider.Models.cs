using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.Straico;

public partial class StraicoProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync<IEnumerable<Model>>(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                using var req = new HttpRequestMessage(HttpMethod.Get, "v2/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Straico API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                // ✅ root is already an array
                var arr = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray()
                    : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("context_length", out var contextLengthEl))
                        model.ContextWindow = contextLengthEl.GetInt32();

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        var inputPrice = pricingEl.TryGetProperty("input", out var inEl)
                                ? inEl.GetRawText() : null;

                        var outputPrice = pricingEl.TryGetProperty("output", out var outEl)
                                ? outEl.GetRawText() : null;

                        if (!string.IsNullOrEmpty(outputPrice)
                            && !string.IsNullOrEmpty(inputPrice)
                            && !outputPrice.Equals("0")
                            && !inputPrice.Equals("0"))
                            model.Pricing = new ModelPricing
                            {
                                Input = decimal.Parse(inputPrice, CultureInfo.InvariantCulture),
                                Output = decimal.Parse(outputPrice, CultureInfo.InvariantCulture)
                            };
                    }

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                var languageModels = models
                    .Where(static model => string.IsNullOrWhiteSpace(model.Type)
                                           || string.Equals(model.Type, "language", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                try
                {
                    models.AddRange(await ListStraicoAgentModelsAsync(cancellationToken));
                }
                catch
                {
                }

                try
                {
                    models.AddRange(await ListStraicoRagModelsAsync(languageModels, cancellationToken));
                }
                catch
                {
                }

                return [.. models.DistinctBy(model => model.Id)];

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IEnumerable<Model>> ListStraicoAgentModelsAsync(CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v0/agent/");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
            return [];

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = doc.RootElement;
        var agents = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                ? dataEl.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        var models = new List<Model>();
        foreach (var agent in agents)
        {
            var id = agent.TryGetString("uuidv4")
                     ?? agent.TryGetString("_id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var name = agent.TryGetString("name") ?? id;
            var defaultLlm = agent.TryGetString("default_llm");
            var tags = new List<string> { "agent", "shortcut", $"agent:{id}" };
            if (!string.IsNullOrWhiteSpace(defaultLlm))
                tags.Add($"model:{defaultLlm}");
            if (!string.IsNullOrWhiteSpace(agent.TryGetString("status")))
                tags.Add($"status:{agent.TryGetString("status")}");
            if (!string.IsNullOrWhiteSpace(agent.TryGetString("visibility")))
                tags.Add($"visibility:{agent.TryGetString("visibility")}");

            models.Add(new Model
            {
                Id = $"agent/{id}".ToModelId(GetIdentifier()),
                Name = name,
                Description = agent.TryGetString("description")
                              ?? (string.IsNullOrWhiteSpace(defaultLlm)
                                  ? $"Straico agent '{name}'."
                                  : $"Straico agent '{name}' backed by {defaultLlm}."),
                OwnedBy = nameof(Straico),
                Type = "language",
                Created = TryGetUnixTimestamp(agent, "createdAt"),
                Tags = [.. tags.Distinct(StringComparer.OrdinalIgnoreCase)]
            });
        }

        return models;
    }

    private async Task<IEnumerable<Model>> ListStraicoRagModelsAsync(IEnumerable<Model> languageModels, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v0/rag/user");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
            return [];

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = doc.RootElement;
        var rags = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                ? dataEl.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        var bases = languageModels
            .Where(static model => !string.IsNullOrWhiteSpace(model.Name))
            .ToList();
        var models = new List<Model>();

        foreach (var rag in rags)
        {
            var ragId = rag.TryGetString("_id");
            if (string.IsNullOrWhiteSpace(ragId))
                continue;

            var ragName = rag.TryGetString("name") ?? ragId;
            var files = rag.TryGetString("original_filename");
            var chunkingMethod = rag.TryGetString("chunking_method");
            var created = TryGetUnixTimestamp(rag, "createdAt");

            foreach (var baseModel in bases)
            {
                var baseModelId = baseModel.Name;
                var tags = new List<string>
                {
                    "rag",
                    "shortcut",
                    $"rag:{ragId}",
                    $"model:{baseModelId}"
                };
                if (!string.IsNullOrWhiteSpace(chunkingMethod))
                    tags.Add($"chunking:{chunkingMethod}");

                models.Add(new Model
                {
                    Id = $"rag/{ragId}/{baseModelId}".ToModelId(GetIdentifier()),
                    Name = $"{ragName} ({baseModelId})",
                    Description = string.IsNullOrWhiteSpace(files)
                        ? $"Straico RAG shortcut for '{ragName}' using {baseModelId}."
                        : $"Straico RAG shortcut for '{ragName}' using {baseModelId}. Files: {files}.",
                    OwnedBy = nameof(Straico),
                    Type = "language",
                    Created = created,
                    ContextWindow = baseModel.ContextWindow,
                    MaxTokens = baseModel.MaxTokens,
                    Pricing = baseModel.Pricing,
                    Tags = [.. tags.Distinct(StringComparer.OrdinalIgnoreCase)]
                });
            }
        }

        return models;
    }

    private static long? TryGetUnixTimestamp(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp.ToUnixTimeSeconds()
            : null;
    }
}
