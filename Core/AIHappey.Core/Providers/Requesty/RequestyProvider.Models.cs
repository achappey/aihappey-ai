using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Requesty;

public partial class RequestyProvider
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
            throw new Exception($"Requesty API error: {err}");
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

            if (el.TryGetProperty("id", out var idEl))
            {
                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                model.Name = idEl.GetString() ?? "";
            }

            if (el.TryGetProperty("context_window", out var contextLengthEl))
                model.ContextWindow = contextLengthEl.GetInt32();

            if (el.TryGetProperty("max_output_tokens", out var maxOutputEl))
                model.MaxTokens = maxOutputEl.GetInt32();

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("description", out var descEl))
                model.Description = descEl.GetString() ?? "";

            decimal? inputPrice = null;
            decimal? outputPrice = null;

            if (el.TryGetProperty("input_price", out var inEl) &&
                inEl.ValueKind == JsonValueKind.Number &&
                inEl.TryGetDecimal(out var inPrice))
            {
                inputPrice = inPrice;
            }

            if (el.TryGetProperty("output_price", out var outEl) &&
                outEl.ValueKind == JsonValueKind.Number &&
                outEl.TryGetDecimal(out var outPrice))
            {
                outputPrice = outPrice;
            }

            if (inputPrice.HasValue &&
                outputPrice.HasValue &&
                inputPrice.Value != 0 &&
                outputPrice.Value != 0)
            {
                model.Pricing = new ModelPricing
                {
                    Input = inputPrice.Value,
                    Output = outputPrice.Value
                };
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}