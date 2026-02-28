using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;
using System.Text;

namespace AIHappey.Core.Providers.ModelsLab;

public partial class ModelsLabProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v4/dreambooth/model_list")
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"ModelsLab API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();
        var root = doc.RootElement;

        var arr = root.EnumerateArray();

        foreach (var el in arr)
        {
            Model model = new();

            if (el.TryGetProperty("model_id", out var idEl))
            {
                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                model.Name = idEl.GetString() ?? "";
            }

            if (el.TryGetProperty("model_name", out var nameEl))
                model.Name = nameEl.GetString() ?? model.Id;

            if (el.TryGetProperty("description", out var descriptionEl))
                model.Description = descriptionEl.GetString() ?? string.Empty;

            model.Type = "image";

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        models.AddRange(GetIdentifier().GetModels());

        return models;
    }
}