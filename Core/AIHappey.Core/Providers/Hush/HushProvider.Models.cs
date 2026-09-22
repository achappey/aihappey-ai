using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Providers.Hush;

public partial class HushProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            ApplyAuthHeader();
            using var response = await _client.GetAsync("v1/models", ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Hush model listing failed ({(int)response.StatusCode}): {raw}");
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
            return data.EnumerateArray().Select(item =>
            {
                var id = ReadHushString(item, "id") ?? string.Empty;
                var input = ReadHushPrice(item, "input");
                var output = ReadHushPrice(item, "output");
                return new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = ReadHushString(item, "display_name") ?? id,
                    OwnedBy = ReadHushString(item, "owned_by") ?? "hush",
                    ContextWindow = ReadHushInt(item, "context_window"),
                    MaxTokens = ReadHushInt(item, "max_output_tokens"),
                    Type = "language",
                    Pricing = input is null && output is null ? null : new ModelPricing { Input = (input ?? 0m) / 1_000_000m / 1_000_000m, Output = (output ?? 0m) / 1_000_000m / 1_000_000m }
                };
            }).Where(model => !string.IsNullOrWhiteSpace(model.Id)).ToList();
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 480, cancellationToken: cancellationToken);
    }

    private static string? ReadHushString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? ReadHushInt(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static decimal? ReadHushPrice(JsonElement item, string name)
    {
        if (!item.TryGetProperty("pricing", out var pricing) || !pricing.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    }
}
