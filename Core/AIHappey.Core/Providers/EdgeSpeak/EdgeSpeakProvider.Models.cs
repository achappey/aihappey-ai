using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.EdgeSpeak;

public sealed partial class EdgeSpeakProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = this.GetCacheKey(_keys.Resolve(ProviderId)) + ":" +
            ModelProviderExtensions.CacheKeyFromApiKey(_client.BaseAddress!.AbsoluteUri);
        var models = await _cache.GetOrCreateAsync<IEnumerable<Model>>(cacheKey, async ct =>
        {
            var catalog = await GetJson("v1/models", ct);
            var result = new List<Model>();
            foreach (var item in catalog.GetProperty("data").EnumerateArray())
            {
                var endpoints = Strings(item, "supported_endpoints");
                var type = endpoints.Any(p => p is "/v1/chat/completions" or "/v1/responses" or "/v1/messages") ? "language"
                    : endpoints.Contains("/v1/audio/speech") ? "speech"
                    : endpoints.Contains("/v1/audio/transcriptions") ? "transcription" : null;
                if (type is null)
                    continue;
                var id = RequiredString(item, "id");
                var tags = Strings(item, "features").Concat(Strings(item, "supported_languages"))
                    .Concat(endpoints).Concat(Strings(item, "default_for").Select(p => $"default:{p}")).ToList();
                if (String(item, "execution_location") is { } location)
                    tags.Add($"execution:{location}");
                if (item.TryGetProperty("x_edgespeak", out var extensions) && extensions.ValueKind == JsonValueKind.Object)
                {
                    if (extensions.TryGetProperty("speech_language", out var language) && language.ValueKind == JsonValueKind.Object &&
                        language.TryGetProperty("open_set", out var openSet) && openSet.ValueKind == JsonValueKind.True)
                        tags.Add("speech_language:open_set");
                    if (extensions.TryGetProperty("music", out var music) && music.ValueKind == JsonValueKind.Object)
                    {
                        tags.AddRange(Strings(music, "planning_modes").Select(mode => $"music:planning_mode:{mode}"));
                        foreach (var flag in new[] { "abc_input", "result_package" })
                            if (music.TryGetProperty(flag, out var enabled) && enabled.ValueKind == JsonValueKind.True)
                                tags.Add($"music:{flag}");
                    }
                }
                result.Add(new()
                {
                    Id = $"{ProviderId}/{id}", Name = String(item, "name") ?? id,
                    OwnedBy = String(item, "owned_by") ?? "EdgeSpeak", Type = type,
                    Created = item.TryGetProperty("created", out var created) ? created.GetInt64() : null,
                    Description = String(item, "description"), Tags = tags
                    // The local service catalog does not advertise per-model prices.
                });
            }
            return result;
        }, baseTtl: TimeSpan.FromMinutes(5), jitterMinutes: 0, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return models;
    }

    private static string[] Strings(JsonElement value, string name)
        => Elements(value, name).Select(v => v.GetString()!).ToArray();
}
