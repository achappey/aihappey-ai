using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.ZenMux;

public partial class ZenMuxProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"ZenMux API error: {err}");
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

            if (el.TryGetProperty("context_length", out var contextLengthEl))
                model.ContextWindow = contextLengthEl.GetInt32();

            if (el.TryGetProperty("display_name", out var nameEl))
                model.Name = nameEl.GetString() ?? model.Id;

            if (el.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number)
                model.Created = createdEl.GetInt64();

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("capabilities", out var capsEl) &&
                capsEl.ValueKind == JsonValueKind.Object)
            {
                var tags = new List<string>();

                foreach (var prop in capsEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.True)
                    {
                        tags.Add(prop.Name);
                    }
                }

                if (tags.Count > 0)
                    model.Tags = tags;
            }

            if (el.TryGetProperty("pricings", out var pricingsEl) &&
      pricingsEl.ValueKind == JsonValueKind.Object)
            {
                decimal? input = null;
                decimal? output = null;

                // ---- PROMPT (input tokens) ----
                if (pricingsEl.TryGetProperty("prompt", out var promptArr) &&
                    promptArr.ValueKind == JsonValueKind.Array &&
                    promptArr.GetArrayLength() > 0)
                {
                    var first = promptArr[0];

                    if (first.TryGetProperty("value", out var valEl) &&
                        first.TryGetProperty("unit", out var unitEl) &&
                        unitEl.GetString() == "perMTokens")
                    {
                        var perMillion = valEl.GetDecimal();
                        input = perMillion / 1_000_000m; // convert to per token
                    }
                }

                // ---- COMPLETION (output tokens) ----
                if (pricingsEl.TryGetProperty("completion", out var compArr) &&
                    compArr.ValueKind == JsonValueKind.Array &&
                    compArr.GetArrayLength() > 0)
                {
                    var first = compArr[0];

                    if (first.TryGetProperty("value", out var valEl) &&
                        first.TryGetProperty("unit", out var unitEl) &&
                        unitEl.GetString() == "perMTokens")
                    {
                        var perMillion = valEl.GetDecimal();
                        output = perMillion / 1_000_000m; // convert to per token
                    }
                }

                if (input.HasValue && output.HasValue &&
                    input.Value > 0 && output.Value > 0)
                {
                    model.Pricing = new ModelPricing
                    {
                        Input = input.Value,
                        Output = output.Value
                    };
                }
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}