using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Providers.Runcrate;

public partial class RuncrateProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
     
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"Runcrate API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var root = doc.RootElement;

                var arr = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray()
                    : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                var models = new List<Model>();

                foreach (var el in arr)
                {
                    var id = GetString(el, "id");

                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var model = new Model
                    {
                        Id = id.ToModelId(GetIdentifier()),
                        Name = GetString(el, "name") ?? id,
                        Description = GetString(el, "description") ?? "",
                        OwnedBy = GetOwner(id),
                        ContextWindow = GetInt(el, "context_length"),
                        MaxTokens = GetInt(el, "max_output_length"),
                        Created = GetLong(el, "created"),
                        Type = GetModelType(el)
                    };

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        var input = GetDecimal(pricingEl, "prompt");
                        var output = GetDecimal(pricingEl, "completion");
                        var cacheRead = GetDecimal(pricingEl, "input_cache_read");

                        if (input is > 0 && output is > 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = input.Value,
                                Output = output.Value,
                                InputCacheRead = cacheRead
                            };
                        }
                    }

                    models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int? GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt32()
            : null;

    private static long? GetLong(JsonElement el, string name)
        => el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt64()
            : null;

    private static decimal? GetDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(
                prop.GetString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var value) => value,
            _ => null
        };
    }

    private static string GetOwner(string id)
    {
        var slashIndex = id.IndexOf('/');

        if (slashIndex <= 0)
            return "";

        return id[..slashIndex];
    }

    private static string GetModelType(JsonElement el)
    {
        if (!el.TryGetProperty("output_modalities", out var modalitiesEl) ||
            modalitiesEl.ValueKind != JsonValueKind.Array)
            return "language";

        var modalities = modalitiesEl
            .EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (modalities.Contains("video"))
            return "video";

        if (modalities.Contains("image"))
            return "image";

        if (modalities.Contains("audio"))
            return "speech";

        return "language";
    }
}