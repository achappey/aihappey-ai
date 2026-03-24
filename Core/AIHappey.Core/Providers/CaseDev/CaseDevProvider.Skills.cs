using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Skills;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.CaseDev;

public partial class CaseDevProvider
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
            async ct => await FetchSkillsFromCaseDev(ct),
            baseTtl: SkillsCacheTtl,
            jitterMinutes: SkillsCacheJitterMinutes,
            cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<SkillVersion>> ListSkillVersions(string skillId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var upstreamSkillId = StripProviderPrefix(skillId);
        var detail = await GetSkillDetailsAsync(upstreamSkillId, cancellationToken);
        var normalizedSkillId = EnsureProviderPrefixedSkillId(detail.Slug ?? upstreamSkillId);
        var version = NormalizeVersion(detail.Version);

        return
        [
            new SkillVersion
            {
                Id = CreateSkillVersionId(normalizedSkillId, version),
                Object = "skill.version",
                CreatedAt = ToUnixTimeSeconds(detail.CreatedAt),
                Description = detail.Summary,
                Name = string.IsNullOrWhiteSpace(detail.Name) ? upstreamSkillId : detail.Name,
                SkillId = normalizedSkillId,
                Version = version
            }
        ];
    }

    public async Task<Stream> RetrieveSkillContent(string skillId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var upstreamSkillId = StripProviderPrefix(skillId);
        var detail = await GetSkillDetailsAsync(upstreamSkillId, cancellationToken);

        return BuildSkillBundle(detail, upstreamSkillId);
    }

    public async Task<Stream> RetrieveSkillVersionContent(string skillId, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var upstreamSkillId = StripProviderPrefix(skillId);
        var detail = await GetSkillDetailsAsync(upstreamSkillId, cancellationToken);

        var requestedVersion = version.Trim();
        var currentVersion = NormalizeVersion(detail.Version);
        if (!string.Equals(requestedVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException($"Skill '{upstreamSkillId}' version '{requestedVersion}' was not found.");

        return BuildSkillBundle(detail, upstreamSkillId);
    }

    private async Task<IEnumerable<Skill>> FetchSkillsFromCaseDev(CancellationToken cancellationToken)
    {
        var allSkills = new List<Skill>();
        string? cursor = null;

        do
        {
            var uri = BuildCustomSkillsListUri(cursor);
            var page = await SendSkillsRequestAsync<CaseDevCustomSkillsPage>(uri, cancellationToken);

            if (page?.Skills != null)
                allSkills.AddRange(page.Skills.Select(MapCustomSkill));

            cursor = page is { HasMore: true } && !string.IsNullOrWhiteSpace(page.NextCursor)
                ? page.NextCursor
                : null;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return [.. allSkills
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Id))
            .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private static string BuildCustomSkillsListUri(string? cursor)
    {
        var baseUri = "https://api.case.dev/skills/custom?limit=100";

        if (string.IsNullOrWhiteSpace(cursor))
            return baseUri;

        return $"{baseUri}&cursor={Uri.EscapeDataString(cursor)}";
    }

    private async Task<CaseDevSkillDetailsResponse> GetSkillDetailsAsync(string slug, CancellationToken cancellationToken)
    {
        var uri = $"https://api.case.dev/skills/{Uri.EscapeDataString(slug)}";
        var detail = await SendSkillsRequestAsync<CaseDevSkillDetailsResponse>(uri, cancellationToken);

        return detail ?? throw new FileNotFoundException($"Skill '{slug}' was not found.");
    }

    private async Task<T?> SendSkillsRequestAsync<T>(string requestUri, CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(CaseDev)} API key.");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"CaseDev skills API error ({(int)response.StatusCode}): {error}");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private Skill MapCustomSkill(CaseDevCustomSkillItem item)
    {
        var slug = item.Slug ?? string.Empty;
        var normalizedSkillId = EnsureProviderPrefixedSkillId(slug);
        var version = NormalizeVersion(item.Version);

        return new Skill
        {
            Id = normalizedSkillId,
            Object = "skill",
            CreatedAt = ToUnixTimeSeconds(item.CreatedAt),
            DefaultVersion = version,
            LatestVersion = version,
            Description = item.Summary,
            Name = string.IsNullOrWhiteSpace(item.Name) ? slug : item.Name
        };
    }

    private Stream BuildSkillBundle(CaseDevSkillDetailsResponse detail, string fallbackSlug)
    {
        var slug = !string.IsNullOrWhiteSpace(detail.Slug)
            ? detail.Slug.Trim()
            : fallbackSlug.Trim();

        var skillMarkdown = BuildSkillMarkdown(detail, slug);

        var bundleStream = new MemoryStream();
        using (var archive = new ZipArchive(bundleStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry($"{slug}/SKILL.md", CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(skillMarkdown);
        }

        bundleStream.Position = 0;
        return bundleStream;
    }

    private static string BuildSkillMarkdown(CaseDevSkillDetailsResponse detail, string fallbackSlug)
    {
        var content = detail.Content ?? string.Empty;
        if (HasYamlFrontmatter(content))
            return content;

        var specName = ToSpecName(detail.Slug, fallbackSlug);
        var description = string.IsNullOrWhiteSpace(detail.Summary)
            ? "Imported from Case.dev custom skill"
            : detail.Summary.Trim();

        var body = string.IsNullOrWhiteSpace(content)
            ? $"# {detail.Name ?? fallbackSlug}\n"
            : content.TrimStart();

        return $"---\nname: {EscapeYamlScalar(specName)}\ndescription: {EscapeYamlScalar(description)}\n---\n\n{body}";
    }

    private static bool HasYamlFrontmatter(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return false;

        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return false;

        var closingIndex = normalized.IndexOf("\n---\n", StringComparison.Ordinal);
        return closingIndex > 4;
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
        => this.GetCacheKey(_keyResolver.Resolve(GetIdentifier())) + SkillsCacheSuffix;

    private static string CreateSkillVersionId(string normalizedSkillId, string version)
        => $"{normalizedSkillId}:{version}";

    private static string NormalizeVersion(JsonElement version)
    {
        if (version.ValueKind == JsonValueKind.String)
            return NormalizeVersion(version.GetString());

        if (version.ValueKind == JsonValueKind.Number)
            return version.GetRawText();

        return "1";
    }

    private static string NormalizeVersion(string? version)
    {
        var normalized = version?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "1" : normalized;
    }

    private static long? ToUnixTimeSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : null;
    }

    private static string ToSpecName(string? slug, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(slug) ? fallback : slug;
        var normalized = source.Trim().ToLowerInvariant();

        var builder = new StringBuilder(normalized.Length);
        var previousWasHyphen = false;

        foreach (var ch in normalized)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                builder.Append(ch);
                previousWasHyphen = false;
            }
            else if (!previousWasHyphen)
            {
                builder.Append('-');
                previousWasHyphen = true;
            }
        }

        var result = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(result))
            return "skill";

        return result.Length > 64 ? result[..64].TrimEnd('-') : result;
    }

    private static string EscapeYamlScalar(string value)
        => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private sealed class CaseDevCustomSkillsPage
    {
        [JsonPropertyName("skills")]
        public List<CaseDevCustomSkillItem>? Skills { get; set; }

        [JsonPropertyName("next_cursor")]
        public string? NextCursor { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }
    }

    private sealed class CaseDevCustomSkillItem
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("version")]
        public JsonElement Version { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public string? UpdatedAt { get; set; }
    }

    private sealed class CaseDevSkillDetailsResponse
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }
    }
}
