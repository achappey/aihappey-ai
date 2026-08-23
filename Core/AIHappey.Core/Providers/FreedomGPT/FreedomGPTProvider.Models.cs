using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AIHappey.Core.Providers.FreedomGPT;

public partial class FreedomGPTProvider
{
    private const string ActorGptModelPrefix = "actor-gpt";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"FreedomGPT API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = new List<Model>();
                var root = doc.RootElement;

                // ✅ root is already an array
                var arr = root.EnumerateArray();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("model", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                models.AddRange(GetIdentifier().GetModels());

                var actorGptCatalog = await GetActorGptCatalogAsync(ct);
                models.AddRange(BuildActorGptModels(actorGptCatalog));

                return models
                    .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                    .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Task<ActorGptCatalog> GetActorGptCatalogAsync(CancellationToken cancellationToken)
        => _memoryCache.GetOrCreateAsync(
            $"{this.GetCacheKey()}:actor-gpt",
            async ct =>
            {
                var actors = await GetActorGptResourcesAsync("v1/actor-gpt/actors", isActor: true, ct);
                var voices = await GetActorGptResourcesAsync("v1/actor-gpt/voices", isActor: false, ct);

                return new ActorGptCatalog(
                    AssignUniqueSlugs(actors),
                    AssignUniqueSlugs(voices));
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);

    private async Task<IReadOnlyList<ActorGptResource>> GetActorGptResourcesAsync(
        string path,
        bool isActor,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(path, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"FreedomGPT ActorGPT catalog request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("FreedomGPT ActorGPT catalog response was not an array.");

        var resources = new List<ActorGptResource>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = ReadActorGptString(item, isActor ? "actorId" : "voiceId")
                ?? ReadActorGptString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            resources.Add(new ActorGptResource(
                id.Trim(),
                ReadActorGptString(item, "name")?.Trim() ?? id.Trim(),
                ReadActorGptString(item, "gender"),
                isActor ? ReadActorGptString(item, "actorType") : ReadActorGptString(item, "voiceType"),
                isActor ? ReadActorGptString(item, "ethnicity") : ReadActorGptLanguages(item),
                Slug: string.Empty));
        }

        return resources
            .GroupBy(resource => resource.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private IEnumerable<Model> BuildActorGptModels(ActorGptCatalog catalog)
    {
        foreach (var actor in catalog.Actors)
        {
            yield return new Model
            {
                Id = $"{ActorGptModelPrefix}/{actor.Slug}".ToModelId(GetIdentifier()),
                Name = $"ActorGPT — {actor.Name}",
                OwnedBy = nameof(FreedomGPT),
                Type = "video",
                Description = BuildActorGptDescription(actor, null),
                Tags = BuildActorGptTags(actor, null)
            };

            foreach (var voice in catalog.Voices)
            {
                yield return new Model
                {
                    Id = $"{ActorGptModelPrefix}/{actor.Slug}/{voice.Slug}".ToModelId(GetIdentifier()),
                    Name = $"ActorGPT — {actor.Name} / {voice.Name}",
                    OwnedBy = nameof(FreedomGPT),
                    Type = "video",
                    Description = BuildActorGptDescription(actor, voice),
                    Tags = BuildActorGptTags(actor, voice)
                };
            }
        }
    }

    private static string BuildActorGptDescription(ActorGptResource actor, ActorGptResource? voice)
        => voice is null
            ? $"Generate an ActorGPT video with actor '{actor.Name}'. Supply voiceId through providerOptions.freedomgpt."
            : $"Generate an ActorGPT video with actor '{actor.Name}' and voice '{voice.Name}'.";

    private static IEnumerable<string> BuildActorGptTags(ActorGptResource actor, ActorGptResource? voice)
    {
        var tags = new List<string> { "avatar", "actor" };
        AddActorGptTag(tags, actor.Gender);
        AddActorGptTag(tags, actor.Kind);
        AddActorGptTag(tags, actor.Detail);
        if (voice is not null)
        {
            tags.Add("voice");
            AddActorGptTag(tags, voice.Gender);
            AddActorGptTag(tags, voice.Kind);
            AddActorGptTag(tags, voice.Detail);
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddActorGptTag(List<string> tags, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            tags.Add(value.Trim().ToLowerInvariant());
    }

    private static IReadOnlyList<ActorGptResource> AssignUniqueSlugs(IReadOnlyList<ActorGptResource> resources)
    {
        var baseSlugs = resources.ToDictionary(resource => resource.Id, resource => SlugifyActorGptName(resource.Name), StringComparer.OrdinalIgnoreCase);
        var duplicateSlugs = baseSlugs.Values
            .GroupBy(slug => slug, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return resources
            .Select(resource =>
            {
                var slug = baseSlugs[resource.Id];
                if (duplicateSlugs.Contains(slug))
                    slug = $"{slug}-{CreateActorGptIdSuffix(resource.Id)}";

                return resource with { Slug = slug };
            })
            .ToList();
    }

    private static string SlugifyActorGptName(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var pendingSeparator = false;

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                    builder.Append('-');
                builder.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = builder.Length > 0;
            }
        }

        return builder.Length == 0 ? "resource" : builder.ToString();
    }

    private static string CreateActorGptIdSuffix(string id)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..8].ToLowerInvariant();

    private static string? ReadActorGptString(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadActorGptLanguages(JsonElement item)
        => item.TryGetProperty("languages", out var languages) && languages.ValueKind == JsonValueKind.Array
            ? string.Join(",", languages.EnumerateArray()
                .Where(language => language.ValueKind == JsonValueKind.String)
                .Select(language => language.GetString())
                .Where(language => !string.IsNullOrWhiteSpace(language)))
            : null;

    private sealed record ActorGptCatalog(
        IReadOnlyList<ActorGptResource> Actors,
        IReadOnlyList<ActorGptResource> Voices);

    private sealed record ActorGptResource(
        string Id,
        string Name,
        string? Gender,
        string? Kind,
        string? Detail,
        string Slug);
}
