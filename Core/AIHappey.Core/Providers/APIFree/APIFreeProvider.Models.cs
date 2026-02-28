using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.APIFree;

public partial class APIFreeProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        ApplyAuthHeader();

        var models = new List<Model>();
        string? cursor = null;

        do
        {
            var url = "v1/models";
            if (!string.IsNullOrEmpty(cursor))
                url += $"?cursor={Uri.EscapeDataString(cursor)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _client.SendAsync(req, cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"APIFree API error: {err}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var root = doc.RootElement;

            if (root.TryGetProperty("models", out var modelsEl) &&
                modelsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in modelsEl.EnumerateArray())
                {
                    if (!el.TryGetProperty("endpoint_id", out var idEl))
                        continue;

                    var endpointId = idEl.GetString();
                    if (string.IsNullOrWhiteSpace(endpointId))
                        continue;

                    var model = new Model
                    {
                        Id = endpointId.ToModelId(GetIdentifier()),
                        Name = endpointId
                    };

                    if (el.TryGetProperty("metadata", out var metaEl) &&
                        metaEl.ValueKind == JsonValueKind.Object)
                    {
                        if (metaEl.TryGetProperty("display_name", out var displayEl))
                            model.Name = displayEl.GetString() ?? model.Name;

                        if (metaEl.TryGetProperty("description", out var descEl))
                            model.Description = descEl.GetString();

                        if (metaEl.TryGetProperty("category", out var catEl))
                        {
                            var cat = catEl.GetString();

                            if (cat?.Equals("llm") == true)
                                model.Type = "language";

                            if (cat?.EndsWith("image") == true)
                                model.Type = "image";

                            if (cat?.EndsWith("video") == true)
                                model.Type = "video";
                        }

                    }

                    models.Add(model);
                }
            }

            cursor = root.TryGetProperty("has_more", out var hasMoreEl) &&
                     hasMoreEl.GetBoolean() &&
                     root.TryGetProperty("next_cursor", out var nextEl)
                ? nextEl.GetString()
                : null;

        } while (!string.IsNullOrEmpty(cursor));

        return models;
    }
}