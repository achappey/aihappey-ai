using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Providers.CanRouter;

public partial class CanRouterProvider
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
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"CanRouter model listing failed ({(int)response.StatusCode}): {raw}");

            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
            return data.EnumerateArray().Select(MapModel).Where(model => !string.IsNullOrWhiteSpace(model.Id)).ToList();
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 480, cancellationToken: cancellationToken);
    }

    private Model MapModel(JsonElement item)
    {
        var id = ReadString(item, "id") ?? string.Empty;
        return new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = ReadString(item, "name") ?? ReadString(item, "display_name") ?? id,
            Description = ReadString(item, "description"),
            OwnedBy = ReadString(item, "owned_by") ?? "canrouter",
            ContextWindow = ReadInt(item, "context_window") ?? ReadInt(item, "context_length"),
            MaxTokens = ReadInt(item, "max_output_tokens") ?? ReadInt(item, "max_tokens"),
            Type = ReadString(item, "type") ?? id.GuessModelType(),
            Pricing = ReadPricing(item)
        };
    }

    private static ModelPricing? ReadPricing(JsonElement item)
    {
        if (!item.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object) return null;
        var input = ReadDecimal(pricing, "input") ?? ReadDecimal(pricing, "prompt");
        var output = ReadDecimal(pricing, "output") ?? ReadDecimal(pricing, "completion");
        return input is null && output is null ? null : new ModelPricing { Input = input ?? 0, Output = output ?? 0 };
    }

    private static string? ReadString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? ReadInt(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static decimal? ReadDecimal(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        if (value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number) ? number : null;
    }
}
