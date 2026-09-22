using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.MintRouter;

public partial class MintRouterProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                ApplyAuthHeader();
                using var response = await _client.GetAsync("v1/models", ct);
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"MintRouter model listing failed ({(int)response.StatusCode}): {raw}");

                using var document = JsonDocument.Parse(raw);
                if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return [];

                return data.EnumerateArray()
                    .Where(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    .Select(item =>
                    {
                        var id = item.GetProperty("id").GetString()!;
                        return new Model
                        {
                            Id = id.ToModelId(GetIdentifier()),
                            Name = id,
                            OwnedBy = item.TryGetProperty("owned_by", out var owner) ? owner.GetString() ?? "mintrouter" : "mintrouter",
                            Type = id.GuessModelType()
                        };
                    })
                    .ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}
