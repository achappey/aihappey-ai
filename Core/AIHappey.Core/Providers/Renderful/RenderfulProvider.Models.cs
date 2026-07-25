using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Renderful;

public partial class RenderfulProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Renderful API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.TryGetProperty("models", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
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

                    if (el.TryGetProperty("provider", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("description", out var descriptionEl))
                        model.Description = descriptionEl.GetString() ?? string.Empty;

                    if (el.TryGetProperty("type", out var typeEl))
                        model.Type = MapRenderfulModelType(typeEl.GetString());

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string MapRenderfulModelType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return string.Empty;

        return type.Trim().ToLowerInvariant() switch
        {
            "text-to-text" => "language",
            "text-to-image" or "image-to-image" or "upscale" or "face-swap" => "image",
            "text-to-video" or "image-to-video" or "video-to-video" or "reference-to-video" or "subject-to-video" or "lip-sync" => "video",
            "text-to-audio" or "text-to-music" or "audio-to-audio" => "speech",
            "speech-to-text" => "transcription",
            _ => type
        };
    }
}
