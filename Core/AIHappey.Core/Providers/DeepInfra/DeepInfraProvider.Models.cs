using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.DeepInfra;

public partial class DeepInfraProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
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
                    throw new Exception($"DeepInfra API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

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
                        model.Type = model.Name.GuessModelType();
                    }
                   
                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                // Both catalogs must succeed before the combined snapshot can be cached.
                using var decisionResp = await _client.GetAsync("typesafe/v1/models", ct);
                if (!decisionResp.IsSuccessStatusCode)
                {
                    var err = await decisionResp.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException($"DeepInfra decision model API error ({(int)decisionResp.StatusCode}): {err}", null, decisionResp.StatusCode);
                }
                await using var decisionStream = await decisionResp.Content.ReadAsStreamAsync(ct);
                using var decisionDoc = await JsonDocument.ParseAsync(decisionStream, cancellationToken: ct);
                var decisionModels = decisionDoc.RootElement.GetProperty("models");
                if (decisionModels.ValueKind != JsonValueKind.Array)
                    throw new JsonException("DeepInfra decision model catalog must contain a models array.");
                var merged = models.ToDictionary(model => model.Id, StringComparer.Ordinal);
                foreach (var el in decisionModels.EnumerateArray())
                {
                    var name = el.GetProperty("name").GetString();
                    if (string.IsNullOrWhiteSpace(name))
                        throw new JsonException("DeepInfra decision model name is required.");
                    var releaseDate = el.GetProperty("release_date").GetString();
                    var decisionModel = new Model
                    {
                        Id = name.ToModelId(GetIdentifier()), Name = name, OwnedBy = GetIdentifier(), Type = "decision",
                        Description = el.GetProperty("description").GetString(),
                        Created = DateTimeOffset.TryParse(releaseDate, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
                            ? date.ToUnixTimeSeconds() : null
                    };
                    merged[decisionModel.Id] = decisionModel;
                }
                return merged.Values.ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}
