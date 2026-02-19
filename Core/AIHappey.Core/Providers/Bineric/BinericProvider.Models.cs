using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Bineric;

public partial class BinericProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);


        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "api/v1/ai/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Bineric API error: {err}");
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
                var id = idEl.GetString();
                model.Id = id?.ToModelId(GetIdentifier()) ?? "";
                model.Name = id ?? "";
            }

            if (el.TryGetProperty("name", out var nameEl))
                model.Name = nameEl.GetString() ?? model.Name;

            if (model.Name == "Speech to Text")
            {
                model.Type = "transcription";
            }

            if (model.Name == "Text to Speech")
            {
                model.Type = "speech";
            }

            if (el.TryGetProperty("description", out var descriptionEl))
                model.Description = descriptionEl.GetString() ?? string.Empty;

            if (el.TryGetProperty("provider", out var providerEl))
                model.OwnedBy = providerEl.GetString() ?? string.Empty;

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;

    }
}