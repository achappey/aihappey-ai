using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.RoutePlex;

public partial class RoutePlexProvider
{
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
                    throw new Exception($"RoutePlex API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                // possible locations where the models array might live
                var arr =
                    root.ValueKind == JsonValueKind.Array
                        ? root.EnumerateArray()
                        : root.TryGetProperty("data", out var dataEl)
                            ? dataEl.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array
                                ? modelsEl.EnumerateArray()
                                : dataEl.ValueKind == JsonValueKind.Array
                                    ? dataEl.EnumerateArray()
                                    : Enumerable.Empty<JsonElement>()
                            : [];


                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("context_window", out var contextLengthEl))
                        model.ContextWindow = contextLengthEl.GetInt32();

                    if (el.TryGetProperty("max_output_tokens", out var mxOutput))
                        model.MaxTokens = mxOutput.GetInt32();

                    if (el.TryGetProperty("display_name", out var orgEl))
                        model.Name = orgEl.GetString() ?? model.Name;

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}