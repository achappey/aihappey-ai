using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Skills;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.ClawHub;

public partial class ClawHubProvider
{
    private const string SkillsCacheSuffix = ":skills";
    private const string SkillDetailsCacheSuffix = ":skill:";
    private const string SkillVersionsCacheSuffix = ":versions:";
    private const int SkillsPageLimit = 100;
    private static readonly TimeSpan SkillsCacheTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan SkillDetailsCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SkillVersionsCacheTtl = TimeSpan.FromMinutes(15);
    private const int SkillsCacheJitterMinutes = 5;
    private const int MaxRequestAttempts = 4;
    private static readonly TimeSpan PageRequestDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly ClawHubSkillListRequest[] CuratedSkillListRequests =
    [
        new("trending", NonSuspiciousOnly: false),
        new("stars", NonSuspiciousOnly: true),
        new("downloads", NonSuspiciousOnly: true)
    ];

    public async Task<IEnumerable<Skill>> ListSkills(CancellationToken cancellationToken = default)
    {
        return await _memoryCache.GetOrCreateAsync(
            GetSkillsCacheKey(),
            FetchSkillsFromClawHub,
            baseTtl: SkillsCacheTtl,
            jitterMinutes: SkillsCacheJitterMinutes,
            cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<SkillVersion>> ListSkillVersions(string skillId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var upstreamSkillId = StripProviderPrefix(skillId);
        await ResolveNonSuspiciousSkillAsync(upstreamSkillId, cancellationToken);

        return await _memoryCache.GetOrCreateAsync(
            GetSkillVersionsCacheKey(upstreamSkillId),
            async ct => await FetchSkillVersionsFromClawHub(upstreamSkillId, ct),
            baseTtl: SkillVersionsCacheTtl,
            jitterMinutes: SkillsCacheJitterMinutes,
            cancellationToken: cancellationToken);
    }

    public async Task<Stream> RetrieveSkillContent(string skillId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var upstreamSkillId = StripProviderPrefix(skillId);
        var skill = await ResolveNonSuspiciousSkillAsync(upstreamSkillId, cancellationToken);
        var version = NormalizeVersion(skill.LatestVersion ?? skill.DefaultVersion);

        return await DownloadSkillBundleAsync(upstreamSkillId, version, cancellationToken);
    }

    public async Task<Stream> RetrieveSkillVersionContent(string skillId, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var upstreamSkillId = StripProviderPrefix(skillId);
        await ResolveNonSuspiciousSkillAsync(upstreamSkillId, cancellationToken);

        return await DownloadSkillBundleAsync(upstreamSkillId, NormalizeVersion(version), cancellationToken);
    }

    private async Task<IEnumerable<Skill>> FetchSkillsFromClawHub(CancellationToken cancellationToken)
    {
        var allSkills = new List<Skill>();

        foreach (var listRequest in CuratedSkillListRequests)
        {
            await DelayBetweenPageRequestsAsync(allSkills.Count, cancellationToken);
            var page = await SendJsonRequestAsync<ClawHubSkillsPage>(BuildSkillsListUri(listRequest), cancellationToken);

            if (page?.Items != null)
                allSkills.AddRange(page.Items.Select(MapSkill));
        }

        return [.. allSkills
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Id))
            .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private async Task<IEnumerable<SkillVersion>> FetchSkillVersionsFromClawHub(string slug, CancellationToken cancellationToken)
    {
        var allVersions = new List<SkillVersion>();
        string? cursor = null;

        do
        {
            await DelayBetweenPageRequestsAsync(allVersions.Count, cancellationToken);
            var page = await SendJsonRequestAsync<ClawHubSkillVersionsPage>(BuildSkillVersionsUri(slug, cursor), cancellationToken);

            if (page?.Items != null)
                allVersions.AddRange(page.Items.Select(version => MapSkillVersion(slug, version)));

            cursor = string.IsNullOrWhiteSpace(page?.NextCursor) ? null : page.NextCursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        if (allVersions.Count == 0)
        {
            var detail = await GetSkillDetailsAsync(slug, cancellationToken);
            if (detail.LatestVersion != null || detail.Tags?.Latest != null)
                allVersions.Add(MapSkillVersion(slug, detail));
        }

        return [.. allVersions
            .Where(version => !string.IsNullOrWhiteSpace(version.Version))
            .GroupBy(version => version.Version, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private async Task<ClawHubSkillItem> GetSkillDetailsAsync(string slug, CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
            GetSkillDetailsCacheKey(slug),
            async ct => await SendJsonRequestAsync<ClawHubSkillItem>(
                $"v1/skills/{Uri.EscapeDataString(slug)}",
                ct) ?? throw new FileNotFoundException($"ClawHub skill '{slug}' was not found."),
            baseTtl: SkillDetailsCacheTtl,
            jitterMinutes: SkillsCacheJitterMinutes,
            cancellationToken: cancellationToken);
    }

    private async Task<Skill> ResolveNonSuspiciousSkillAsync(string slug, CancellationToken cancellationToken)
    {
        var normalizedSkillId = EnsureProviderPrefixedSkillId(slug);
        var skills = await ListSkills(cancellationToken);
        var skill = skills.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, normalizedSkillId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(StripProviderPrefix(candidate.Id), slug, StringComparison.OrdinalIgnoreCase));

        return skill ?? throw new FileNotFoundException($"ClawHub skill '{slug}' was not found in the non-suspicious catalog.");
    }

    private async Task<MemoryStream> DownloadSkillBundleAsync(string slug, string version, CancellationToken cancellationToken)
    {
        using var response = await SendAnonymousRequestAsync(
            BuildDownloadUri(slug, version),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bundleStream = new MemoryStream();
        await sourceStream.CopyToAsync(bundleStream, cancellationToken);
        bundleStream.Position = 0;
        return bundleStream;
    }

    private async Task<T?> SendJsonRequestAsync<T>(string requestUri, CancellationToken cancellationToken)
    {
        using var response = await SendAnonymousRequestAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAnonymousRequestAsync(
        string requestUri,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxRequestAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = null;

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, completionOption, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < MaxRequestAttempts)
            {
                await DelayBeforeRetryAsync(attempt, retryAfter: null, cancellationToken);
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaxRequestAttempts)
            {
                await DelayBeforeRetryAsync(attempt, retryAfter: null, cancellationToken);
                continue;
            }

            if (response.IsSuccessStatusCode)
                return response;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                throw new FileNotFoundException($"ClawHub resource '{requestUri}' was not found.");
            }

            if (IsTransientStatusCode(response.StatusCode) && attempt < MaxRequestAttempts)
            {
                var retryAfter = GetRetryAfterDelay(response);
                response.Dispose();
                await DelayBeforeRetryAsync(attempt, retryAfter, cancellationToken);
                continue;
            }

            using (response)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new HttpRequestException($"ClawHub API rate limit exceeded. {GetRateLimitMessage(response)} {error}".Trim());

                throw new HttpRequestException($"ClawHub API error ({(int)response.StatusCode}): {error}");
            }
        }

        throw new HttpRequestException($"ClawHub API request failed after {MaxRequestAttempts.ToString(CultureInfo.InvariantCulture)} attempts: {requestUri}");
    }

    private static async Task DelayBetweenPageRequestsAsync(int receivedItemCount, CancellationToken cancellationToken)
    {
        if (receivedItemCount <= 0)
            return;

        await Task.Delay(AddJitter(PageRequestDelay), cancellationToken);
    }

    private static async Task DelayBeforeRetryAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var exponentialDelay = TimeSpan.FromMilliseconds(BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var delay = retryAfter.HasValue && retryAfter.Value > exponentialDelay ? retryAfter.Value : exponentialDelay;

        if (delay > MaxRetryDelay)
            delay = MaxRetryDelay;

        await Task.Delay(AddJitter(delay), cancellationToken);
    }

    private static TimeSpan AddJitter(TimeSpan delay)
        => delay + TimeSpan.FromMilliseconds(Random.Shared.Next(100, 500));

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests
           || statusCode == HttpStatusCode.RequestTimeout
           || (int)statusCode >= 500;

    private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        if (TryGetHeader(response, "RateLimit-Reset", out var resetDelay)
            && int.TryParse(resetDelay, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        if (TryGetHeader(response, "X-RateLimit-Reset", out var resetEpoch)
            && long.TryParse(resetEpoch, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochSeconds))
        {
            var resetTime = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            var delay = resetTime - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private static string BuildSkillsListUri(ClawHubSkillListRequest listRequest)
    {
        var uri = $"v1/skills?limit={SkillsPageLimit.ToString(CultureInfo.InvariantCulture)}&sort={Uri.EscapeDataString(listRequest.Sort)}";
        return listRequest.NonSuspiciousOnly ? $"{uri}&nonSuspiciousOnly=true" : uri;
    }

    private static string BuildSkillVersionsUri(string slug, string? cursor)
    {
        var uri = $"v1/skills/{Uri.EscapeDataString(slug)}/versions?limit={SkillsPageLimit.ToString(CultureInfo.InvariantCulture)}";
        return string.IsNullOrWhiteSpace(cursor)
            ? uri
            : $"{uri}&cursor={Uri.EscapeDataString(cursor)}";
    }

    private static string BuildDownloadUri(string slug, string version)
        => $"v1/download?slug={Uri.EscapeDataString(slug)}&version={Uri.EscapeDataString(version)}";

    private Skill MapSkill(ClawHubSkillItem item)
    {
        var slug = item.Slug ?? string.Empty;
        var normalizedSkillId = EnsureProviderPrefixedSkillId(slug);
        var version = NormalizeVersion(item.LatestVersion?.Version ?? item.Tags?.Latest);

        return new Skill
        {
            Id = normalizedSkillId,
            Object = "skill",
            CreatedAt = ToUnixTimeSeconds(item.CreatedAt),
            DefaultVersion = version,
            LatestVersion = version,
            Description = item.Summary,
            Name = string.IsNullOrWhiteSpace(item.DisplayName) ? slug : item.DisplayName
        };
    }

    private SkillVersion MapSkillVersion(string slug, ClawHubSkillVersionItem item)
    {
        var normalizedSkillId = EnsureProviderPrefixedSkillId(slug);
        var version = NormalizeVersion(item.Version);

        return new SkillVersion
        {
            Id = CreateSkillVersionId(normalizedSkillId, version),
            Object = "skill.version",
            CreatedAt = ToUnixTimeSeconds(item.CreatedAt),
            Description = item.Changelog,
            Name = slug,
            SkillId = normalizedSkillId,
            Version = version
        };
    }

    private SkillVersion MapSkillVersion(string slug, ClawHubSkillItem detail)
    {
        var normalizedSkillId = EnsureProviderPrefixedSkillId(detail.Slug ?? slug);
        var version = NormalizeVersion(detail.LatestVersion?.Version ?? detail.Tags?.Latest);

        return new SkillVersion
        {
            Id = CreateSkillVersionId(normalizedSkillId, version),
            Object = "skill.version",
            CreatedAt = ToUnixTimeSeconds(detail.LatestVersion?.CreatedAt ?? detail.UpdatedAt ?? detail.CreatedAt),
            Description = detail.LatestVersion?.Changelog ?? detail.Summary,
            Name = string.IsNullOrWhiteSpace(detail.DisplayName) ? slug : detail.DisplayName,
            SkillId = normalizedSkillId,
            Version = version
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
        => $"{GetIdentifier()}{SkillsCacheSuffix}:nonsuspicious";

    private string GetSkillDetailsCacheKey(string slug)
        => $"{GetIdentifier()}{SkillDetailsCacheSuffix}{slug.ToLowerInvariant()}:nonsuspicious";

    private string GetSkillVersionsCacheKey(string slug)
        => $"{GetIdentifier()}{SkillVersionsCacheSuffix}{slug.ToLowerInvariant()}:nonsuspicious";

    private static string CreateSkillVersionId(string normalizedSkillId, string version)
        => $"{normalizedSkillId}:{version}";

    private static string NormalizeVersion(string? version)
    {
        var normalized = version?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "1" : normalized;
    }

    private static long? ToUnixTimeSeconds(long? milliseconds)
        => milliseconds.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value).ToUnixTimeSeconds() : null;

    private static string GetRateLimitMessage(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return $"Retry after {delta.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.";

        if (response.Headers.RetryAfter?.Date is { } date)
            return $"Retry after {date:O}.";

        if (TryGetHeader(response, "RateLimit-Reset", out var resetDelay))
            return $"Retry after {resetDelay} seconds.";

        if (TryGetHeader(response, "X-RateLimit-Reset", out var resetEpoch))
            return $"Rate limit resets at Unix epoch second {resetEpoch}.";

        return string.Empty;
    }

    private static bool TryGetHeader(HttpResponseMessage response, string headerName, out string value)
    {
        if (response.Headers.TryGetValues(headerName, out var values))
        {
            value = values.FirstOrDefault() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        value = string.Empty;
        return false;
    }

    private sealed record ClawHubSkillListRequest(string Sort, bool NonSuspiciousOnly);

    private sealed class ClawHubSkillsPage
    {
        [JsonPropertyName("items")]
        public List<ClawHubSkillItem>? Items { get; set; }

        [JsonPropertyName("nextCursor")]
        public string? NextCursor { get; set; }
    }

    private sealed class ClawHubSkillVersionsPage
    {
        [JsonPropertyName("items")]
        public List<ClawHubSkillVersionItem>? Items { get; set; }

        [JsonPropertyName("nextCursor")]
        public string? NextCursor { get; set; }
    }

    private sealed class ClawHubSkillItem
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("tags")]
        public ClawHubSkillTags? Tags { get; set; }

        [JsonPropertyName("createdAt")]
        public long? CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public long? UpdatedAt { get; set; }

        [JsonPropertyName("latestVersion")]
        public ClawHubSkillVersionItem? LatestVersion { get; set; }
    }

    private sealed class ClawHubSkillTags
    {
        [JsonPropertyName("latest")]
        public string? Latest { get; set; }
    }

    private sealed class ClawHubSkillVersionItem
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("createdAt")]
        public long? CreatedAt { get; set; }

        [JsonPropertyName("changelog")]
        public string? Changelog { get; set; }

        [JsonPropertyName("license")]
        public string? License { get; set; }
    }
}
