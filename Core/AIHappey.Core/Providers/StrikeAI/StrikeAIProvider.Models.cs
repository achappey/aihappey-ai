using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.StrikeAI;

public sealed partial class StrikeAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyAuthHeader();
        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(),
            async ct =>
            {
                using var response = await _client.GetAsync("v1/models", ct);
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"StrikeAI model discovery failed ({(int)response.StatusCode}): {raw}", null, response.StatusCode);

                using var document = JsonDocument.Parse(raw);
                var data = document.RootElement.GetProperty("data");
                if (data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("StrikeAI model discovery requires a data array.");
                var models = new Dictionary<string, Model>(StringComparer.Ordinal);
                foreach (var item in data.EnumerateArray())
                {
                    var nativeId = item.GetProperty("id").GetString();
                    if (string.IsNullOrWhiteSpace(nativeId))
                        throw new JsonException("StrikeAI model IDs cannot be empty.");
                    var model = item.Deserialize<Model>(JsonSerializerOptions.Web)
                        ?? throw new JsonException("StrikeAI returned an invalid model.");
                    model.Id = nativeId.ToModelId(GetIdentifier());
                    model.Name = string.IsNullOrWhiteSpace(model.Name) ? nativeId : model.Name;
                    model.OwnedBy = string.IsNullOrWhiteSpace(model.OwnedBy) ? "strike" : model.OwnedBy;
                    model.Type = "decision";
                    if (item.TryGetProperty("max_context_tokens", out var context))
                    {
                        if (!context.TryGetInt32(out var tokens) || tokens <= 0)
                            throw new JsonException("Invalid StrikeAI model context limit.");
                        model.ContextWindow = tokens;
                    }
                    if (!models.TryAdd(model.Id, model))
                        throw new JsonException("Duplicate StrikeAI model ID.");
                }
                return models.Values.ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}
