using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SambaNova;

public partial class SambaNovaProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();
        var root = doc.RootElement;

        // ✅ root is already an array
        var arr = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                ? dataEl.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        foreach (var el in arr)
        {
            if (!el.TryGetProperty("id", out var idEl))
                continue;

            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id))
                continue;

            var model = new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = id,
                Type = id.Contains("whisper", StringComparison.OrdinalIgnoreCase)
                    ? "transcription" : "language",
                OwnedBy = el.TryGetProperty("owned_by", out var ownedByEl)
                    ? ownedByEl.GetString() ?? ""
                    : "",
            };

            if (el.TryGetProperty("context_length", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Number)
                model.ContextWindow = ctxEl.GetInt32();

            if (el.TryGetProperty("max_completion_tokens", out var maxTokEl) && maxTokEl.ValueKind == JsonValueKind.Number)
                model.MaxTokens = maxTokEl.GetInt32();

            if (el.TryGetProperty("pricing", out var pricingEl) &&
                pricingEl.ValueKind == JsonValueKind.Object &&
                pricingEl.TryGetProperty("prompt", out var inEl) &&
                pricingEl.TryGetProperty("completion", out var outEl))
            {
                model.Pricing = new ModelPricing
                {
                    Input = inEl.ToString().NormalizeTokenPrice(),
                    Output = outEl.ToString().NormalizeTokenPrice()
                };
            }

            models.Add(model);

            if (model.Type == "transcription")
            {
                models.Add(new Model()
                {
                    Id = model.Id + "/translate",
                    Name = model.Name + " Translate to English",
                    Description = model.Description,
                    OwnedBy = model.OwnedBy,
                    Pricing = model.Pricing,
                    MaxTokens = model.MaxTokens

                });
            }
        }

        return models;
    }
}