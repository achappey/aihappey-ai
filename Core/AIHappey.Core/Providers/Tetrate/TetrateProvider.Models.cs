using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Tetrate;

public partial class TetrateProvider
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
            throw new Exception($"Tetrate API error: {err}");
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
            Model model = new();

            if (el.TryGetProperty("id", out var idEl))
            {
                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                model.Name = idEl.GetString() ?? "";
            }

            if (el.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number)
                model.Created = createdEl.GetInt64();

            if (el.TryGetProperty("context_window", out var contextLengthEl))
                model.ContextWindow = contextLengthEl.GetInt32();

            if (el.TryGetProperty("max_output_tokens", out var maxOutputEl))
                model.MaxTokens = maxOutputEl.GetInt32();

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            // pricing lives in root
            decimal? input = null;
            decimal? output = null;
            decimal? cacheRead = null;
            decimal? cacheWrite = null;

            if (el.TryGetProperty("input_price", out var inputEl))
            {
                var str = inputEl.GetString();
                if (decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                    input = val;
            }

            if (el.TryGetProperty("output_price", out var outputEl))
            {
                var str = outputEl.GetString();
                if (decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                    output = val;
            }

            if (el.TryGetProperty("cached_price", out var cachedEl))
            {
                var str = cachedEl.GetString();
                if (decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                    cacheRead = val;
            }

            if (el.TryGetProperty("caching_price", out var cachingEl))
            {
                var str = cachingEl.GetString();
                if (decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                    cacheWrite = val;
            }

            if (input is > 0 && output is > 0)
            {
                model.Pricing = new ModelPricing
                {
                    Input = input.Value,
                    Output = output.Value,
                    InputCacheRead = cacheRead,
                    InputCacheWrite = cacheWrite
                };
            }

            // ensure tags list exists
            var tags = new List<string>();

            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Name.StartsWith("supports_", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.True)
                {
                    var tag = prop.Name["supports_".Length..];

                    if (!string.IsNullOrWhiteSpace(tag))
                        tags.Add(tag);
                }
            }

            if (tags.Count != 0)
            {
                model.Tags = tags;
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}