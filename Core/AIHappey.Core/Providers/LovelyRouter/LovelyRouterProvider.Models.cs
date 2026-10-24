using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.LovelyRouter;

public sealed partial class LovelyRouterProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => asyncCacheHelper.GetOrCreateAsync<IEnumerable<Model>>(
            this.GetCacheKey(),
            async ct =>
            {
                using var response = await _client.GetAsync("v1/models", ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"Lovely Router model discovery failed ({(int)response.StatusCode}): {error}",
                        null, response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return document.RootElement.GetProperty("data").EnumerateArray()
                    .Select(item =>
                    {
                        var id = item.GetProperty("id").GetString();
                        if (string.IsNullOrWhiteSpace(id))
                            throw new JsonException("Lovely Router returned a model without an id.");

                        var pricing = item.GetProperty("pricing");
                        var input = pricing.GetProperty("input").GetDecimal();
                        return new Model
                        {
                            Id = id.ToModelId(GetIdentifier()),
                            Name = item.GetProperty("display_name").GetString() ?? id,
                            OwnedBy = item.GetProperty("owned_by").GetString() ?? GetIdentifier(),
                            Type = "language",
                            Created = item.TryGetProperty("created", out var created) ? created.GetInt64() : null,
                            ContextWindow = item.GetProperty("context_length").GetInt32(),
                            Pricing = new ModelPricing
                            {
                                Input = input / 1_000_000m,
                                Output = pricing.GetProperty("output").GetDecimal() / 1_000_000m,
                                InputCacheRead = CachePrice(pricing, "cache_read", input),
                                InputCacheWrite = CachePrice(pricing, "cache_write", input)
                            },
                            Metadata = item.EnumerateObject()
                                .Where(p => p.Name is not ("id" or "object" or "type" or "owned_by" or "created" or "pricing"))
                                .ToDictionary(p => p.Name, p => p.Value.Clone())
                        };
                    }).ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);

    private static decimal CachePrice(JsonElement pricing, string name, decimal input)
        => (pricing.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetDecimal()
            : input) / 1_000_000m;
}
