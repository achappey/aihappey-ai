using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.DeAPI;

public partial class DeAPIProvider
{
    private async Task<IEnumerable<Model>> ListModelsDeapi(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var models = new List<Model>();
                var page = 1;
                var lastPage = 1;
                do
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"api/v2/models?page={page}");
                    using var resp = await _client.SendAsync(req, ct);
                    var raw = await resp.Content.ReadAsStringAsync(ct);
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"DeAPI models failed ({(int)resp.StatusCode}): {raw}");

                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
                    {
                        if (meta.TryGetProperty("last_page", out var last) && last.TryGetInt32(out var parsedLast))
                            lastPage = parsedLast;
                    }

                    var items = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

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
                    page++;
                } while (page <= lastPage);

                models.AddRange(await this.ListModels(_keyResolver.Resolve(GetIdentifier())));
                return models;

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string ResolveModelType(JsonElement item)
    {
        var inferenceType = item.TryGetProperty("inference_type", out var itEl) && itEl.ValueKind == JsonValueKind.String
            ? itEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(inferenceType)
            && item.TryGetProperty("inference_types", out var typesEl)
            && typesEl.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            inferenceType = typesEl.ValueKind == JsonValueKind.Object
                ? typesEl.EnumerateObject().FirstOrDefault().Name
                : typesEl.EnumerateArray().FirstOrDefault(a => a.ValueKind == JsonValueKind.String).GetString();
        }

        return inferenceType switch
        {
            "txt2img" or "img2img" or "img-rmbg" or "img-upscale" => "image",
            "txt2audio" or "txt2music" => "speech",
            "video2text" or "video_file2text" or "audio2text" or "audio_file2text" or "img2txt" => "transcription",
            "txt2video" or "img2video" or "audio2video" or "vid-upscale" or "video-replace" => "video",
            "txt2embedding" or "embedding" => "embedding",
            _ => "language"
        };
    }
}

