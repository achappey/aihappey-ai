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

    public async Task<Stream> RetrieveSkillVersionContent(string skillId, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var upstreamSkillId = StripProviderPrefix(skillId);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/skills/{Uri.EscapeDataString(upstreamSkillId)}/versions/{Uri.EscapeDataString(version)}/content");
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

    private static string BuildSkillVersionsListUri(string skillId, string? after)
    {
        var baseUri = $"v1/skills/{Uri.EscapeDataString(skillId)}/versions";

        if (string.IsNullOrWhiteSpace(after))
            return baseUri;

        return $"{baseUri}?after={Uri.EscapeDataString(after)}";
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

            var page = await response.Content.ReadFromJsonAsync<DataList<Skill>>(cancellationToken: cancellationToken);
            if (page?.Data != null)
                skills.AddRange(page.Data.Select(PrefixSkillId));

            after = page is { HasMore: true } && !string.IsNullOrWhiteSpace(page.LastId)
                ? StripProviderPrefix(page.LastId)
                : null;
        }
        while (!string.IsNullOrWhiteSpace(after));

        return [.. skills
            .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
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


    public async Task<IEnumerable<SkillVersion>> ListSkillVersions(string skillId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        return await FetchSkillVersionsFromOpenAI(skillId, cancellationToken);
    }

    private async Task<IEnumerable<SkillVersion>> FetchSkillVersionsFromOpenAI(string skillId, CancellationToken cancellationToken)
    {
        var upstreamSkillId = StripProviderPrefix(skillId);
        var versions = new List<SkillVersion>();
        string? after = null;

        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildSkillVersionsListUri(upstreamSkillId, after));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var page = await response.Content.ReadFromJsonAsync<DataList<SkillVersion>>(cancellationToken: cancellationToken);
            if (page?.Data != null)
                versions.AddRange(page.Data.Select(version => PrefixSkillVersion(version, skillId)));

            after = page is { HasMore: true } && !string.IsNullOrWhiteSpace(page.LastId)
                ? page.LastId
                : null;
        }
        while (!string.IsNullOrWhiteSpace(after));

        return [.. versions
            .GroupBy(version => version.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private SkillVersion PrefixSkillVersion(SkillVersion version, string fallbackSkillId)
    {
        var normalizedSkillId = fallbackSkillId;
        var skillId = version.SkillId ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(skillId))
        {
            if (!skillId.Contains('/', StringComparison.Ordinal))
            {
                normalizedSkillId = skillId.ToModelId(GetIdentifier());
            }
            else
            {
                var split = skillId.SplitModelId();
                if (string.Equals(split.Provider, GetIdentifier(), StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(split.Model))
                {
                    normalizedSkillId = skillId;
                }
                else
                {
                    normalizedSkillId = skillId.ToModelId(GetIdentifier());
                }
            }
        }

        if (string.Equals(normalizedSkillId, version.SkillId, StringComparison.Ordinal))
            return version;

        return new SkillVersion
        {
            Id = version.Id,
            Object = version.Object,
            CreatedAt = version.CreatedAt,
            Description = version.Description,
            Name = version.Name,
            SkillId = normalizedSkillId,
            Version = version.Version
        };
    }
}
