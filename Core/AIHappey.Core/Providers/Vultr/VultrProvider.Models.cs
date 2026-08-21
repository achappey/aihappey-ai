using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Vultr;

public partial class VultrProvider
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
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Vultr API error: {err}");
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
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                var baseModels = models.ToList();
                var voices = await ListVultrVoicesAsync(cancellationToken);
                foreach (var speech in baseModels.Where(IsVultrSpeechModel))
                {
                    foreach (var voice in voices)
                        AddVultrModelIfMissing(models, new Model
                        {
                            Id = $"{speech.Id}/{voice}", Name = $"{speech.Name}/{voice}", OwnedBy = speech.OwnedBy,
                            Type = "speech", Description = $"Vultr speech model '{speech.Name}' with voice '{voice}'.", Tags = ["voice"]
                        });
                    speech.Type = "speech";
                }

                var collections = await ListVultrCollectionsAsync(cancellationToken);
                foreach (var language in baseModels.Where(model => !IsVultrSpeechModel(model) && !IsVultrImageModel(model)))
                {
                    language.Type ??= "language";
                    foreach (var collection in collections)
                        AddVultrModelIfMissing(models, new Model
                        {
                            Id = $"{language.Id}/{collection.Id}", Name = $"{language.Name}/{collection.Id}", OwnedBy = language.OwnedBy,
                            Type = "language", Description = $"Vultr RAG model '{language.Name}' using vector-store collection '{collection.Name}' ({collection.Id}).",
                            Tags = ["rag", "vector-store"]
                        });
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<List<string>> ListVultrVoicesAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("audio/voices", cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in document.RootElement.EnumerateObject())
            if (group.Value.ValueKind == JsonValueKind.Array)
                foreach (var voice in group.Value.EnumerateArray())
                    if (voice.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(voice.GetString())) result.Add(voice.GetString()!);
        return [.. result];
    }

    private async Task<List<VultrCollection>> ListVultrCollectionsAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("vector_store", cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("collections", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        return items.EnumerateArray().Select(x => new VultrCollection(
            x.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            x.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "")).Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToList();
    }

    private static bool IsVultrSpeechModel(Model model) => model.Name.Contains("bark", StringComparison.OrdinalIgnoreCase)
        || model.Name.Contains("xtts", StringComparison.OrdinalIgnoreCase) || model.Name.Contains("tts", StringComparison.OrdinalIgnoreCase);
    private static bool IsVultrImageModel(Model model) => model.Name.Contains("flux", StringComparison.OrdinalIgnoreCase)
        || model.Name.Contains("stable-diffusion", StringComparison.OrdinalIgnoreCase);
    private static void AddVultrModelIfMissing(List<Model> models, Model model)
    { if (!models.Any(x => string.Equals(x.Id, model.Id, StringComparison.OrdinalIgnoreCase))) models.Add(model); }
    private sealed record VultrCollection(string Id, string Name);
}
