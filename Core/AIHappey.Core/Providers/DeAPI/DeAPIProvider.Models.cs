using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.DeAPI;

public partial class DeAPIProvider
{
    private async Task<IEnumerable<Model>> ListModelsDeapi(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "api/v1/client/models?per_page=200&page=1");
        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeAPI models failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var items = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
            ? dataEl.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

        var models = new List<Model>();
        foreach (var item in items)
        {
            var slug = item.TryGetProperty("slug", out var slugEl) && slugEl.ValueKind == JsonValueKind.String
                ? slugEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(slug))
                continue;

            var name = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? slug
                : slug;

            var description = item.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
                ? descEl.GetString()
                : null;

            var type = ResolveModelType(item);

            models.Add(new Model
            {
                Id = slug.ToModelId(GetIdentifier()),
                Name = name,
                Description = description,
                Type = type,
                OwnedBy = "deapi.ai"
            });
        }

        models.AddRange(await this.ListModels(_keyResolver.Resolve(GetIdentifier())));
        return models;
    }

    private static string ResolveModelType(JsonElement item)
    {
        var inferenceType = item.TryGetProperty("inference_type", out var itEl) && itEl.ValueKind == JsonValueKind.String
            ? itEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(inferenceType)
            && item.TryGetProperty("inference_types", out var typesEl)
            && typesEl.ValueKind == JsonValueKind.Array)
        {
            inferenceType = typesEl.EnumerateArray()
                .FirstOrDefault(a => a.ValueKind == JsonValueKind.String)
                .GetString();
        }

        return inferenceType switch
        {
            "txt2img" or "img2img" or "img-rmbg" => "image",
            "txt2audio" => "speech",
            "vid2txt" or "video2text" or "videofile2txt" or "aud2txt" or "audio2text" or "audiofile2txt" or "img2txt" => "transcription",
            "txt2video" or "img2video" or "vid-rmbg" => "video",
            "txt2embedding" => "embedding",
            _ => "language"
        };
    }
}

