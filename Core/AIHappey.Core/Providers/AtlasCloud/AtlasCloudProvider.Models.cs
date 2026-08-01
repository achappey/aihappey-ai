using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Reflection;

namespace AIHappey.Core.Providers.AtlasCloud;

public partial class AtlasCloudProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {

                using var req = new HttpRequestMessage(HttpMethod.Get, "https://console.atlascloud.ai/api/v1/models");
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var assemblyName = assembly.GetName();
                var userAgent = $"{assemblyName.Name}/{assemblyName.Version}";

                req.Headers.TryAddWithoutValidation("User-Agent", userAgent);

                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"AtlasCloud API error: {err}");
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

                    if (el.TryGetProperty("model", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    model.ContextWindow = el.TryGetProperty("contextLength", out var v) &&
                        v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : null;

                    model.MaxTokens = el.TryGetProperty("maxCompletionTokens", out var m) &&
                       m.ValueKind == JsonValueKind.Number
                            ? m.GetInt32()
                            : null;

                    if (el.TryGetProperty("displayName", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("organization", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("profile", out var profileEl))
                        model.Description = profileEl.GetString() ?? "";

                    if (el.TryGetProperty("type", out var typeEl))
                        model.Type = typeEl.GetString()?.ToLower() ?? model.Id.GuessModelType();
                    
                    if (model.Type.Equals("text"))
                        model.Type = "language";

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