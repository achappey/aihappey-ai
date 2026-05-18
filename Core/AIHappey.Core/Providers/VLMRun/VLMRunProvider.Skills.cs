using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Skills;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.VLMRun;

public partial class VLMRunProvider
{
    private const string SkillsCacheSuffix = ":skills";
    private const string SkillDetailsCacheSuffix = ":skill:";
    private const int SkillsPageLimit = 1000;
    private static readonly TimeSpan SkillsCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SkillDetailsCacheTtl = TimeSpan.FromMinutes(15);
    private const int SkillsCacheJitterMinutes = 5;

    public async Task<IEnumerable<Skill>> ListSkills(CancellationToken cancellationToken = default)
    {
        return await _memoryCache.GetOrCreateAsync(
            GetSkillsCacheKey(),
            FetchSkillsFromVLMRun,
            baseTtl: SkillsCacheTtl,
            jitterMinutes: SkillsCacheJitterMinutes,
            cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<SkillVersion>> ListSkillVersions(string skillId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var upstreamSkillId = StripProviderPrefix(skillId);
        var detail = await GetSkillDetailsAsync(upstreamSkillId, cancellationToken);
        var normalizedSkillId = EnsureProviderPrefixedSkillId(detail.Id ?? upstreamSkillId);
        var version = NormalizeVersion(detail.SkillVersion);

        return
        [
            new SkillVersion
            {
                Id = CreateSkillVersionId(normalizedSkillId, version),
                Object = "skill.version",
                CreatedAt = ToUnixTimeSeconds(detail.CreatedAt),
                Description = detail.Description,
                Name = string.IsNullOrWhiteSpace(detail.Name) ? upstreamSkillId : detail.Name,
                SkillId = normalizedSkillId,
                Version = version
            }
        ];
    }

    public async Task<Stream> RetrieveSkillContent(string skillId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        return await DownloadSkillBundleAsync(StripProviderPrefix(skillId), cancellationToken);
    }

    public async Task<Stream> RetrieveSkillVersionContent(string skillId, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var upstreamSkillId = StripProviderPrefix(skillId);
        var detail = await GetSkillDetailsAsync(upstreamSkillId, cancellationToken);
        var requestedVersion = version.Trim();
        var currentVersion = NormalizeVersion(detail.SkillVersion);

        if (!string.Equals(requestedVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException($"Skill '{upstreamSkillId}' version '{requestedVersion}' was not found.");

        return await DownloadSkillBundleAsync(upstreamSkillId, cancellationToken);
    }

    private async Task<IEnumerable<Skill>> FetchSkillsFromVLMRun(CancellationToken cancellationToken)
    {
        var allSkills = new List<Skill>();
        var offset = 0;

        while (true)
        {
            var page = await SendVLMRunJsonRequestAsync<List<VLMRunSkillInfoResponse>>(
                BuildSkillsListUri(offset),
                cancellationToken);

            if (page == null || page.Count == 0)
                break;

            allSkills.AddRange(page.Select(MapSkill));

            if (page.Count < SkillsPageLimit)
                break;

            offset += page.Count;
        }

        return [.. allSkills
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Id))
            .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private async Task<VLMRunSkillInfoResponse> GetSkillDetailsAsync(string skillId, CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
            GetSkillDetailsCacheKey(skillId),
            async ct => await SendVLMRunJsonRequestAsync<VLMRunSkillInfoResponse>(
                $"v1/skills/{Uri.EscapeDataString(skillId)}",
                ct) ?? throw new FileNotFoundException($"VLMRun skill '{skillId}' was not found."),
            baseTtl: SkillDetailsCacheTtl,
            jitterMinutes: SkillsCacheJitterMinutes,
            cancellationToken: cancellationToken);
    }

    private async Task<MemoryStream> DownloadSkillBundleAsync(string skillId, CancellationToken cancellationToken)
    {
        var download = await SendVLMRunJsonRequestAsync<VLMRunSkillDownloadResponse>(
            $"v1/skills/{Uri.EscapeDataString(skillId)}/download",
            cancellationToken) ?? throw new FileNotFoundException($"VLMRun skill '{skillId}' download URL was not found.");

        if (string.IsNullOrWhiteSpace(download.DownloadUrl))
            throw new FileNotFoundException($"VLMRun skill '{skillId}' download URL was empty.");

        using var request = new HttpRequestMessage(HttpMethod.Get, download.DownloadUrl);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bundleStream = new MemoryStream();
        await sourceStream.CopyToAsync(bundleStream, cancellationToken);
        bundleStream.Position = 0;

        return bundleStream;
    }

    private async Task<T?> SendVLMRunJsonRequestAsync<T>(string requestUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        ApplyOptionalAuthHeader(request);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"VLMRun skills API error ({(int)response.StatusCode}): {error}");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private void ApplyOptionalAuthHeader(HttpRequestMessage request)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static string BuildSkillsListUri(int offset)
        => $"v1/skills?limit={SkillsPageLimit.ToString(CultureInfo.InvariantCulture)}&offset={offset.ToString(CultureInfo.InvariantCulture)}&grouped=true";

    private Skill MapSkill(VLMRunSkillInfoResponse item)
    {
        var skillId = item.Id ?? string.Empty;
        var normalizedSkillId = EnsureProviderPrefixedSkillId(skillId);
        var version = NormalizeVersion(item.SkillVersion);

        return new Skill
        {
            Id = normalizedSkillId,
            Object = "skill",
            CreatedAt = ToUnixTimeSeconds(item.CreatedAt),
            DefaultVersion = version,
            LatestVersion = version,
            Description = item.Description,
            Name = string.IsNullOrWhiteSpace(item.Name) ? skillId : item.Name
        };
    }

    private string EnsureProviderPrefixedSkillId(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId))
            return skillId;

        if (!skillId.Contains('/', StringComparison.Ordinal))
            return skillId.ToModelId(GetIdentifier());

        var split = skillId.SplitModelId();
        return string.Equals(split.Provider, GetIdentifier(), StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(split.Model)
            ? skillId
            : skillId.ToModelId(GetIdentifier());
    }

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

    private string GetSkillsCacheKey()
        => this.GetCacheKey(_keyResolver.Resolve(GetIdentifier()) ?? "guest") + SkillsCacheSuffix;

    private string GetSkillDetailsCacheKey(string skillId)
        => $"{GetSkillsCacheKey()}{SkillDetailsCacheSuffix}{skillId.ToLowerInvariant()}";

    private static string CreateSkillVersionId(string normalizedSkillId, string version)
        => $"{normalizedSkillId}:{version}";

    private static string NormalizeVersion(string? version)
    {
        var normalized = version?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "latest" : normalized;
    }

    private static long? ToUnixTimeSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : null;
    }

    private sealed class VLMRunSkillInfoResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("skill_version")]
        public string? SkillVersion { get; set; }

        [JsonPropertyName("skill_uri")]
        public string? SkillUri { get; set; }

        [JsonPropertyName("is_public")]
        public bool IsPublic { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public string? UpdatedAt { get; set; }
    }

    private sealed class VLMRunSkillDownloadResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("skill_version")]
        public string? SkillVersion { get; set; }

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
