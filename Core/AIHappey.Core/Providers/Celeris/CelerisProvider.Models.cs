using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Celeris;

public sealed partial class CelerisProvider
{
    public async Task<IEnumerable<Model>> ListCelerisModelsAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(),
            async ct =>
            {
                var models = new Dictionary<string, Model>(StringComparer.Ordinal);
                foreach (var (pathModel, isDecision) in new[]
                {
                    ("celeris-1", false),
                    ("celeris-1-decision", true)
                })
                {
                    using var response = await _client.GetAsync($"{pathModel}/v1/models", ct);
                    var raw = await response.Content.ReadAsStringAsync(ct);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"Celeris model discovery failed ({(int)response.StatusCode}): {raw}",
                            null,
                            response.StatusCode);

                    using var document = JsonDocument.Parse(raw);
                    var data = document.RootElement.GetProperty("data");
                    if (data.ValueKind != JsonValueKind.Array)
                        throw new JsonException("Celeris model catalog must contain a data array.");

                    foreach (var item in data.EnumerateArray())
                    {
                        var nativeId = item.GetProperty("id").GetString();
                        if (string.IsNullOrWhiteSpace(nativeId))
                            throw new JsonException("Celeris model IDs cannot be empty.");

                        if (nativeId == "celeris-1-decision")
                            nativeId = "celeris-1.1-decision";

                        var model = JsonSerializer.Deserialize<Model>(item, JsonSerializerOptions.Web)
                            ?? throw new JsonException("Celeris returned an invalid model.");
                        model.Id = nativeId.ToModelId(GetIdentifier());
                        model.Name = string.IsNullOrWhiteSpace(model.Name) ? nativeId : model.Name;
                        model.OwnedBy = string.IsNullOrWhiteSpace(model.OwnedBy) ? GetIdentifier() : model.OwnedBy;
                        model.Type = isDecision ? "decision" : string.IsNullOrWhiteSpace(model.Type) ? "language" : model.Type;
                        models[model.Id] = model;
                    }
                }

                foreach (var model in GetIdentifier().GetModels())
                    models[model.Id] = model;

                return models.Values.ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}
