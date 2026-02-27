using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AIML;

public partial class AIMLProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"AI/ML API error: {err}");
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

            var model = new Model
            {
                Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? ""
            };

            if (string.IsNullOrEmpty(model.Id))
                continue;

            // type
            if (el.TryGetProperty("type", out var typeEl))
            {
                var type = typeEl.GetString();
                model.Type = type == "chat-completion" ? "language" :
                    type == "responses" ? "language" :
                    type == "tts" ? "speech" :
                    type == "stt" ? "transcription"
                    : model.Id.Contains("music")
                    ? "speech" : type ?? "";
            }

            // info block
            if (el.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
            {
                if (infoEl.TryGetProperty("name", out var nameEl))
                    model.Name = nameEl.GetString() ?? model.Id;

                if (infoEl.TryGetProperty("contextLength", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Number)
                    model.ContextWindow = ctxEl.GetInt32();

                if (infoEl.TryGetProperty("developer", out var devEl))
                    model.OwnedBy = devEl.GetString() ?? "";
            }

            models.Add(model);
        }

        return models.Where(a => a.Type != "document"
            && a.Type != "language-completion");
    }
}