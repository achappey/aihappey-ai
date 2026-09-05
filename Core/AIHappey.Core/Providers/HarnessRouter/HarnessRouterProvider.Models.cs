using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;
using System.Net.Http.Json;

namespace AIHappey.Core.Providers.HarnessRouter;

public partial class HarnessRouterProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(HarnessRouter)} API key.");

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                ApplyAuthHeader();
                using var response = await _client.GetAsync("v1/models", ct);
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"HarnessRouter model catalog error ({(int)response.StatusCode}): {raw}",
                        null,
                        response.StatusCode);

                using var document = JsonDocument.Parse(raw);
                if (!document.RootElement.TryGetProperty("backends", out var backends)
                    || backends.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("HarnessRouter returned an invalid model catalog.");

                var models = new List<Model>();
                foreach (var backend in backends.EnumerateObject())
                {
                    if (backend.Value.ValueKind != JsonValueKind.Object)
                        continue;

                    var harness = BackendToHarness(backend.Name);
                    var defaultModel = ReadCatalogModelId(backend.Value, "default");
                    models.Add(CreateHarnessModel(harness, null, defaultModel, true));

                    if (!backend.Value.TryGetProperty("models", out var backendModels)
                        || backendModels.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var entry in backendModels.EnumerateArray())
                    {
                        var modelId = entry.ValueKind == JsonValueKind.String
                            ? entry.GetString()
                            : ReadCatalogModelId(entry, "id") ?? ReadCatalogModelId(entry, "model");
                        if (string.IsNullOrWhiteSpace(modelId))
                            continue;

                        var name = entry.ValueKind == JsonValueKind.Object
                            ? ReadCatalogModelId(entry, "name")
                            : null;
                        var description = entry.ValueKind == JsonValueKind.Object
                            ? ReadCatalogModelId(entry, "description")
                            : null;

                        models.Add(CreateHarnessModel(
                            harness,
                            modelId,
                            name,
                            string.Equals(modelId, defaultModel, StringComparison.Ordinal),
                            description));
                    }
                }

                return (IEnumerable<Model>)models
                    .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            },
            TimeSpan.FromMinutes(15),
            jitterMinutes: 2,
            cancellationToken);
    }

    private Model CreateHarnessModel(
        string harness,
        string? backendModel,
        string? displayName,
        bool isDefault,
        string? description = null)
    {
        var id = $"{GetIdentifier()}/{harness}";
        if (!string.IsNullOrWhiteSpace(backendModel))
            id += $"/{backendModel}";

        return new Model
        {
            Id = id,
            OwnedBy = "HarnessRouter",
            Name = !string.IsNullOrWhiteSpace(displayName)
                ? $"{HarnessDisplayName(harness)} · {displayName}"
                : HarnessDisplayName(harness),
            Description = description
                ?? (isDefault
                    ? $"HarnessRouter {HarnessDisplayName(harness)} harness using its default model."
                    : $"HarnessRouter {HarnessDisplayName(harness)} harness using {backendModel}."),
            Type = "language",
            Tags = isDefault ? ["agent", "harness", "default"] : ["agent", "harness"]
        };
    }

    private static string BackendToHarness(string backend)
        => string.Equals(backend, "claude", StringComparison.OrdinalIgnoreCase)
            ? "claude-code"
            : backend;

    private static string HarnessDisplayName(string harness)
        => string.Equals(harness, "claude-code", StringComparison.OrdinalIgnoreCase)
            ? "Claude Code"
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(harness.Replace('-', ' '));

    private static string? ReadCatalogModelId(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
