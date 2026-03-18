using System.Net.Http.Headers;
using System.Net.Http.Json;
using AIHappey.Common.Model.Skills;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    private const string SkillsCacheSuffix = ":skills";
    private static readonly TimeSpan SkillsCacheTtl = TimeSpan.FromMinutes(15);
    private const int SkillsCacheJitterMinutes = 5;

    public async Task<IEnumerable<Skill>> ListSkills(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        return await _memoryCache.GetOrCreateAsync(
            GetSkillsCacheKey(),
            async ct => await FetchSkillsFromOpenAI(ct),
            baseTtl: SkillsCacheTtl,
            jitterMinutes: SkillsCacheJitterMinutes,
            cancellationToken: cancellationToken);
    }

    public async Task<Stream> RetrieveSkillContent(string skillId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var upstreamSkillId = StripProviderPrefix(skillId);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/skills/{Uri.EscapeDataString(upstreamSkillId)}/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bundleStream = new MemoryStream();
        await responseStream.CopyToAsync(bundleStream, cancellationToken);
        bundleStream.Position = 0;

        return bundleStream;
    }

    private static string BuildSkillsListUri(string? after)
    {
        if (string.IsNullOrWhiteSpace(after))
            return "v1/skills";

        return $"v1/skills?after={Uri.EscapeDataString(after)}";
    }

    private async Task<IEnumerable<Skill>> FetchSkillsFromOpenAI(CancellationToken cancellationToken)
    {
        var skills = new List<Skill>();
        string? after = null;

        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildSkillsListUri(after));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var page = await response.Content.ReadFromJsonAsync<SkillList>(cancellationToken: cancellationToken);
            if (page?.Data != null)
                skills.AddRange(page.Data.Select(PrefixSkillId));

            after = page is { HasMore: true } && !string.IsNullOrWhiteSpace(page.LastId)
                ? StripProviderPrefix(page.LastId)
                : null;
        }
        while (!string.IsNullOrWhiteSpace(after));

        return skills
            .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private Skill PrefixSkillId(Skill skill)
    {
        var id = skill.Id ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
            return skill;

        var normalizedId = id;
        if (!id.Contains('/', StringComparison.Ordinal))
        {
            normalizedId = id.ToModelId(GetIdentifier());
        }
        else
        {
            var split = id.SplitModelId();
            if (!string.Equals(split.Provider, GetIdentifier(), StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(split.Model))
            {
                normalizedId = id.ToModelId(GetIdentifier());
            }
        }

        if (string.Equals(normalizedId, skill.Id, StringComparison.Ordinal))
            return skill;

        return new Skill
        {
            Id = normalizedId,
            Object = skill.Object,
            CreatedAt = skill.CreatedAt,
            DefaultVersion = skill.DefaultVersion,
            Description = skill.Description,
            LatestVersion = skill.LatestVersion,
            Name = skill.Name
        };
    }

    private string GetSkillsCacheKey()
        => this.GetCacheKey(_keyResolver.Resolve(GetIdentifier())) + SkillsCacheSuffix;

    private string StripProviderPrefix(string skillId)
    {
        if (!skillId.Contains('/', StringComparison.Ordinal))
            return skillId;

        var split = skillId.SplitModelId();
        return string.Equals(split.Provider, GetIdentifier(), StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(split.Model)
            ? split.Model
            : skillId;
    }
}
