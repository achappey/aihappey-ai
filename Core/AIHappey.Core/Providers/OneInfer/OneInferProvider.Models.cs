using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.OneInfer;

public partial class OneInferProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/developer/get-all-models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"OneInfer API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();

                if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                    !dataEl.TryGetProperty("models", out var modelsEl) ||
                    modelsEl.ValueKind != JsonValueKind.Array)
                    return models;

                foreach (var el in modelsEl.EnumerateArray())
                {
                    if (!el.TryGetProperty("model_name", out var nameEl))
                        continue;

                    var name = nameEl.GetString();
                    if (string.IsNullOrEmpty(name))
                        continue;

                    var model = new Model
                    {
                        Id = name.ToModelId(GetIdentifier()),
                        Name = name,
                        OwnedBy = name.Split('/')[0],
                        Description = el.TryGetProperty("description", out var descEl)
                            ? descEl.GetString()
                            : null
                    };

                    if (el.TryGetProperty("model_context_length", out var ctxEl))
                    {
                        var raw = ctxEl.GetString();

                        if (!string.IsNullOrEmpty(raw))
                        {
                            raw = raw.Replace("K", "000", StringComparison.OrdinalIgnoreCase);

                            if (int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var ctx))
                                model.ContextWindow = ctx;
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
}