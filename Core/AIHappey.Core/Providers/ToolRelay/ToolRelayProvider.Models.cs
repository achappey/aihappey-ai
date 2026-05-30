using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.ToolRelay;

public partial class ToolRelayProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
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


                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"ToolRelay API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.TryGetProperty("models", out var modelsEl) &&
                          modelsEl.ValueKind == JsonValueKind.Array
                    ? modelsEl.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    if (!el.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                        continue;

                    var id = idEl.GetString();
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var typeRaw = el.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                        ? typeEl.GetString()
                        : null;

                    var mappedType = MapType(typeRaw);
                    if (mappedType is null)
                        continue;

                    models.Add(new Model
                    {
                        Id = id.ToModelId(GetIdentifier()),
                        Name = el.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                            ? nameEl.GetString() ?? id
                            : id,
                        Description = el.TryGetProperty("description", out var descriptionEl) && descriptionEl.ValueKind == JsonValueKind.String
                            ? descriptionEl.GetString()
                            : null,
                        OwnedBy = el.TryGetProperty("vendor", out var vendorEl) && vendorEl.ValueKind == JsonValueKind.String
                            ? vendorEl.GetString() ?? "toolrelay"
                            : "toolrelay",
                        Type = mappedType
                    });
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string? MapType(string? type)
        => type?.ToLowerInvariant() switch
        {
            "chat" => "language",
            "tts" => "speech",
            "stt" => "transcription",
            "image" => "image",
            "video" => "video",
            _ => null
        };
}
