using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.GitHub;

public partial class GitHubProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "catalog/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"GitHub Models API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var el in root.EnumerateArray())
        {
            Model model = new();

            // id + name
            if (el.TryGetProperty("id", out var idEl))
            {
                var rawId = idEl.GetString();
                model.Id = rawId?.ToModelId(GetIdentifier()) ?? "";
            }

            if (el.TryGetProperty("name", out var nameEl))
                model.Name = nameEl.GetString() ?? "";

            // publisher -> OwnedBy
            if (el.TryGetProperty("publisher", out var pubEl))
                model.OwnedBy = pubEl.GetString() ?? "";

            // summary -> Description
            if (el.TryGetProperty("summary", out var summaryEl))
                model.Description = summaryEl.GetString();

            // tags
            if (el.TryGetProperty("tags", out var tagsEl) &&
                tagsEl.ValueKind == JsonValueKind.Array)
            {
                model.Tags = [.. tagsEl.EnumerateArray()
                                    .Select(x => x.GetString()!)
                                    .Where(x => !string.IsNullOrEmpty(x))];
            }


            if (el.TryGetProperty("capabilities", out var capEl) &&
                        capEl.ValueKind == JsonValueKind.Array)
            {
                model.Tags = [..model.Tags ?? [], ..capEl.EnumerateArray()
                                        .Select(x => x.GetString()!)
                                        .Where(x => !string.IsNullOrEmpty(x))];
            }

            if (el.TryGetProperty("limits", out var limitsEl) &&
        limitsEl.ValueKind == JsonValueKind.Object)
            {
                if (limitsEl.TryGetProperty("max_input_tokens", out var inTok) &&
                    inTok.ValueKind == JsonValueKind.Number)
                {
                    model.ContextWindow = inTok.GetInt32();
                }

                if (limitsEl.TryGetProperty("max_output_tokens", out var outTok) &&
                    outTok.ValueKind == JsonValueKind.Number)
                {
                    model.MaxTokens = outTok.GetInt32();
                }
            }

            // modalities + capabilities -> Type
            var typeParts = new List<string>();

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}