using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Astria;

public sealed partial class AstriaProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        var catalog = await GetCatalogAsync(cancellationToken);
        var models = new List<Model>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (catalog.TryGetProperty("models", out var images) && images.ValueKind == JsonValueKind.Object)
            foreach (var entry in images.EnumerateObject())
            {
                var id = TuneId(entry.Value);
                if (id is not null && ids.Add(id))
                    models.Add(CatalogModel(id, entry.Name, "image", entry.Value));
            }
        if (catalog.TryGetProperty("video_model_families", out var videos) && videos.ValueKind == JsonValueKind.Object)
            foreach (var entry in videos.EnumerateObject())
                if (!string.IsNullOrWhiteSpace(entry.Name) && entry.Value.ValueKind == JsonValueKind.Object && ids.Add(entry.Name))
                    models.Add(CatalogModel(entry.Name, Text(entry.Value, "title") ?? entry.Name, "video", entry.Value));
        return models;
    }

    private Model CatalogModel(string id, string name, string type, JsonElement entry)
        => new()
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = name,
            OwnedBy = GetIdentifier(),
            Type = type
        };

    private Task<JsonElement> GetCatalogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _cache.GetOrCreateAsync(
            this.GetCacheKey(_keyResolver.Resolve(GetIdentifier()) ?? string.Empty),
            ct => SendAsync(HttpMethod.Get, "cli/models", null, ct, authenticate: false),
            TimeSpan.FromHours(4), cancellationToken: cancellationToken);
    }

    private async Task<string> ResolveTuneAsync(string model, CancellationToken cancellationToken)
    {
        var id = NormalizeModel(model);
        if (PositiveId(id) is { } numeric)
            return numeric;
        var catalog = await GetCatalogAsync(cancellationToken);
        if (catalog.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object
            && models.TryGetProperty(id, out var entry) && TuneId(entry) is { } tune)
            return tune;
        throw new ArgumentException($"Unknown Astria image model '{model}'.");
    }

    private static string? TuneId(JsonElement entry)
        => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("tune_id", out var id)
            ? PositiveId(id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString())
            : null;

    private static string? PositiveId(string? value)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number.ToString(CultureInfo.InvariantCulture) : null;

    private string NormalizeModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var value = model.Trim();
        return value.StartsWith("astria/", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
    }
}
