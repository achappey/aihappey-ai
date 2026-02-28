using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Net;

namespace AIHappey.Core.Providers.APIyi;

public partial class APIyiProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var modelClient = new HttpClient(handler);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.apiyi.com/api/model-info");
        using var resp = await modelClient.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"APIyi API error: {err}");
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

            if (el.TryGetProperty("model_name", out var idEl))
            {
                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                model.Name = idEl.GetString() ?? "";
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}