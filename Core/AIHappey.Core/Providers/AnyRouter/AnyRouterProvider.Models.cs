using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AnyRouter;

public partial class AnyRouterProvider
{
    private static string GetAnyRouterModelType(string nativeId, JsonElement model)
    {
        if (nativeId.Equals("anyrouter/decision", StringComparison.OrdinalIgnoreCase)
            || nativeId.Equals("typesafe/jev", StringComparison.OrdinalIgnoreCase)
            || nativeId.StartsWith("typesafe/jev-", StringComparison.OrdinalIgnoreCase))
            return "decision";
        if (nativeId.Contains("rerank", StringComparison.OrdinalIgnoreCase))
            return "rerank";

        if (model.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() is { Length: > 0 } modelType)
            return modelType;

        return nativeId.GuessModelType();
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {

                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"AnyRouter API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();
                    string? nativeId = null;

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        nativeId = idEl.GetString();
                        model.Id = nativeId?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = nativeId ?? "";
                    }

                    model.ContextWindow = el.TryGetProperty("context_length", out var v) &&
                        v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : null;

                    if (el.TryGetProperty("provider", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("description", out var descriptionEl))
                        model.Description = descriptionEl.GetString() ?? "";

                    if (!string.IsNullOrEmpty(model.Id))
                    {
                        model.Type = GetAnyRouterModelType(nativeId!, el);
                        models.Add(model);
                    }
                }

                var ids = models.Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                models.AddRange(GetIdentifier().GetModels().Where(model => ids.Add(model.Id)));
                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}