using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.PreAPI;

public partial class PreAPIProvider
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
                    throw new Exception($"PreAPI API error: {err}");
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
                    var slug = el.TryGetProperty("slug", out var slugEl) && slugEl.ValueKind == JsonValueKind.String
                        ? slugEl.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(slug))
                        continue;

                    var name = el.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString()
                        : null;

                    var description = el.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
                        ? descEl.GetString()
                        : null;

                    var outputType = el.TryGetProperty("output_type", out var outputTypeEl) && outputTypeEl.ValueKind == JsonValueKind.String
                        ? outputTypeEl.GetString()
                        : null;

                    models.Add(new Model
                    {
                        Id = slug.ToModelId(GetIdentifier()),
                        Name = string.IsNullOrWhiteSpace(name) ? slug : name,
                        Description = description ?? string.Empty,
                        Type = ResolveModelType(outputType),
                        OwnedBy = "preapi.net"
                    });
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string ResolveModelType(string? outputType)
        => outputType?.Trim().ToLowerInvariant() switch
        {
            "image" => "image",
            "video" => "video",
            "audio" => "speech",
            _ => "language"
        };
}
