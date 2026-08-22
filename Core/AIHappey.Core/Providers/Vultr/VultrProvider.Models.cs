using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Vultr;

public partial class VultrProvider
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

                using var req = new HttpRequestMessage(HttpMethod.Get, "models/all");
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"Vultr API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = new List<Model>();
                var root = doc.RootElement;

                AddVultrModels(root, "chat", "language", models);
                AddVultrModels(root, "audio", "speech", models);
                AddVultrModels(root, "image", "image", models);

                var baseModels = models.ToList();
                var voices = await ListVultrVoicesAsync(ct);
                foreach (var speech in baseModels.Where(model => model.Type == "speech"))
                {
                    foreach (var voice in voices)
                        AddVultrModelIfMissing(models, new Model
                        {
                            Id = $"{speech.Id}/{voice}",
                            Name = $"{speech.Name}/{voice}",
                            OwnedBy = speech.OwnedBy,
                            Type = "speech",
                            Description = $"Vultr speech model '{speech.Name}' with voice '{voice}'.",
                            Tags = ["voice"]
                        });
                }

                var collections = await ListVultrCollectionsAsync(ct);
                foreach (var language in baseModels.Where(model => model.Type == "language"))
                {
                    foreach (var collection in collections)
                        AddVultrModelIfMissing(models, new Model
                        {
                            Id = $"{language.Id}/{collection.Id}",
                            Name = $"{language.Name}/{collection.Id}",
                            OwnedBy = language.OwnedBy,
                            Type = "language",
                            Description = $"Vultr RAG model '{language.Name}' using vector-store collection '{collection.Name}' ({collection.Id}).",
                            Tags = ["rag", "vector-store"]
                        });
                }

                
                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private void AddVultrModels(JsonElement root, string propertyName, string type, List<Model> models)
    {
        if (!root.TryGetProperty(propertyName, out var items) || items.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idElement.GetString()))
                continue;

            var id = idElement.GetString()!;
            var ownedBy = item.TryGetProperty("owned_by", out var ownerElement)
                && ownerElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(ownerElement.GetString())
                    ? ownerElement.GetString()!
                    : nameof(Vultr);

            AddVultrModelIfMissing(models, new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = id,
                OwnedBy = ownedBy,
                Object = ReadVultrString(item, "object") ?? "model",
                Created = ReadVultrUnixTimestamp(item),
                Type = type,
                Tags = ReadVultrFeatures(item)
            });
        }
    }

    private static string? ReadVultrString(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadVultrUnixTimestamp(JsonElement item)
    {
        if (!item.TryGetProperty("created", out var created))
            return null;

        if (created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var numericValue))
            return numericValue;

        return created.ValueKind == JsonValueKind.String && long.TryParse(created.GetString(), out var stringValue)
            ? stringValue
            : null;
    }

    private static IEnumerable<string>? ReadVultrFeatures(JsonElement item)
    {
        if (!item.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            return null;

        var values = features.EnumerateArray()
            .Where(feature => feature.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(feature.GetString()))
            .Select(feature => feature.GetString()!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return values.Length == 0 ? null : values;
    }

    private async Task<List<string>> ListVultrVoicesAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("audio/voices", cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in document.RootElement.EnumerateObject())
            if (group.Value.ValueKind == JsonValueKind.Array)
                foreach (var voice in group.Value.EnumerateArray())
                    if (voice.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(voice.GetString())) result.Add(voice.GetString()!);
        return [.. result];
    }

    private async Task<List<VultrCollection>> ListVultrCollectionsAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("vector_store", cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("collections", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        return items.EnumerateArray().Select(x => new VultrCollection(
            x.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            x.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "")).Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToList();
    }

    private static void AddVultrModelIfMissing(List<Model> models, Model model)
    { if (!models.Any(x => string.Equals(x.Id, model.Id, StringComparison.OrdinalIgnoreCase))) models.Add(model); }
    private sealed record VultrCollection(string Id, string Name);
}
