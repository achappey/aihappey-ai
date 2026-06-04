using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Pixserp;

public partial class PixserpProvider
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
                    throw new Exception($"Pixserp API error: {err}");
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

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    var depth = el.TryGetProperty("depth", out var depthEl)
                        ? depthEl.GetString()
                        : null;

                    var price = el.TryGetProperty("price_per_request_usd", out var priceEl)
                        ? priceEl.GetDecimal()
                        : (decimal?)null;

                    var maxToolCalls = el.TryGetProperty("max_tool_calls", out var toolCallsEl)
                        ? toolCallsEl.GetInt32()
                        : (int?)null;

                    var parts = new List<string>();

                    if (!string.IsNullOrWhiteSpace(depth))
                        parts.Add($"{depth} depth.");

                    if (price is not null)
                        parts.Add($"${price:0.####} per request.");

                    if (maxToolCalls is not null)
                        parts.Add($"Max {maxToolCalls:N0} tool calls.");

                    model.Description = string.Join(" ", parts);

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