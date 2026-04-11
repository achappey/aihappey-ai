using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Skills;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.GrooveDev;

public partial class GrooveDevProvider
{
    private const string SkillsCacheSuffix = ":skills";
    private static readonly TimeSpan SkillsCacheTtl = TimeSpan.FromHours(2);
    private const int SkillsCacheJitterMinutes = 5;
    private const string SkillsListUri = "api/v1/skills";
    private const string SyntheticLatestVersion = "latest";

    public async Task<IEnumerable<Skill>> ListSkills(CancellationToken cancellationToken = default)
    {
        var catalog = await GetSkillCatalogAsync(cancellationToken);

        return [.. catalog
            .Select(MapSkill)
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Id))
            .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    public async Task<IEnumerable<SkillVersion>> ListSkillVersions(string skillId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var upstreamSkillId = StripProviderPrefix(skillId);
        var detail = await GetSkillCatalogItemAsync(upstreamSkillId, cancellationToken);
        var normalizedSkillId = EnsureProviderPrefixedSkillId(detail.Id ?? upstreamSkillId);

        return
        [
            new SkillVersion
            {
                Id = CreateSkillVersionId(normalizedSkillId, SyntheticLatestVersion),
                Object = "skill.version",
                CreatedAt = ToUnixTimeSeconds(detail.UpdatedAt) ?? ToUnixTimeSeconds(detail.CreatedAt),
                Description = detail.Description,
                Name = string.IsNullOrWhiteSpace(detail.Name) ? upstreamSkillId : detail.Name,
                SkillId = normalizedSkillId,
                Version = SyntheticLatestVersion
            }
        ];
    }

    public async Task<Stream> RetrieveSkillContent(string skillId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var upstreamSkillId = StripProviderPrefix(skillId);
        var detail = await FindSkillCatalogItemAsync(upstreamSkillId, cancellationToken);
        var content = await GetSkillContentAsync(upstreamSkillId, cancellationToken);

        return BuildSkillBundle(upstreamSkillId, detail, content);
    }

    public async Task<Stream> RetrieveSkillVersionContent(string skillId, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var upstreamSkillId = StripProviderPrefix(skillId);
        var detail = await GetSkillCatalogItemAsync(upstreamSkillId, cancellationToken);

        if (!IsSupportedVersion(version, detail.Version))
            throw new FileNotFoundException($"Skill '{upstreamSkillId}' version '{version.Trim()}' was not found.");

        var content = await GetSkillContentAsync(upstreamSkillId, cancellationToken);

        return BuildSkillBundle(upstreamSkillId, detail, content);
    }

    private async Task<IReadOnlyList<GrooveDevSkillCatalogItem>> GetSkillCatalogAsync(CancellationToken cancellationToken)
        => await _memoryCache.GetOrCreateAsync(
            GetSkillsCacheKey(),
            async ct => await FetchSkillsFromGrooveDev(ct),
            baseTtl: SkillsCacheTtl,
            jitterMinutes: SkillsCacheJitterMinutes,
            cancellationToken: cancellationToken);

    private async Task<IReadOnlyList<GrooveDevSkillCatalogItem>> FetchSkillsFromGrooveDev(CancellationToken cancellationToken)
    {
        var response = await SendSkillsRequestAsync<GrooveDevSkillsResponse>(SkillsListUri, cancellationToken);

        return [.. (response?.Skills ?? [])
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Id))
            .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private async Task<GrooveDevSkillCatalogItem?> FindSkillCatalogItemAsync(string skillId, CancellationToken cancellationToken)
    {
        var catalog = await GetSkillCatalogAsync(cancellationToken);

        return catalog.FirstOrDefault(skill => string.Equals(skill.Id, skillId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<GrooveDevSkillCatalogItem> GetSkillCatalogItemAsync(string skillId, CancellationToken cancellationToken)
        => await FindSkillCatalogItemAsync(skillId, cancellationToken)
           ?? throw new FileNotFoundException($"Skill '{skillId}' was not found.");

    private async Task<GrooveDevSkillContentResponse> GetSkillContentAsync(string skillId, CancellationToken cancellationToken)
    {
        var uri = $"api/v1/skills/{Uri.EscapeDataString(skillId)}/content";
        var content = await SendSkillsRequestAsync<GrooveDevSkillContentResponse>(uri, cancellationToken);

        return content ?? throw new FileNotFoundException($"Skill '{skillId}' content was not found.");
    }

    private async Task<T?> SendSkillsRequestAsync<T>(string requestUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"GrooveDev skills API error ({(int)response.StatusCode}): {error}");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private Skill MapSkill(GrooveDevSkillCatalogItem item)
    {
        var skillId = item.Id ?? string.Empty;

        return new Skill
        {
            Id = EnsureProviderPrefixedSkillId(skillId),
            Object = "skill",
            CreatedAt = ToUnixTimeSeconds(item.CreatedAt),
            DefaultVersion = SyntheticLatestVersion,
            LatestVersion = SyntheticLatestVersion,
            Description = item.Description,
            Name = string.IsNullOrWhiteSpace(item.Name) ? skillId : item.Name
        };
    }

    private Stream BuildSkillBundle(string requestedSkillId, GrooveDevSkillCatalogItem? detail, GrooveDevSkillContentResponse content)
    {
        var skillSlug = requestedSkillId.Trim();
        var skillMarkdown = BuildSkillMarkdown(detail, content, skillSlug);

        var bundleStream = new MemoryStream();
        using (var archive = new ZipArchive(bundleStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry($"{skillSlug}/SKILL.md", CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(skillMarkdown);
        }

        bundleStream.Position = 0;
        return bundleStream;
    }

    private static string BuildSkillMarkdown(GrooveDevSkillCatalogItem? detail, GrooveDevSkillContentResponse content, string fallbackSlug)
    {
        var skillSlug = fallbackSlug.Trim();
        var description = string.IsNullOrWhiteSpace(detail?.Description)
            ? "Imported from GrooveDev skill"
            : detail.Description.Trim();

        var displayName = !string.IsNullOrWhiteSpace(detail?.Name)
            ? detail.Name
            : skillSlug;

        var markdown = StripYamlFrontmatter(content.Content ?? string.Empty);

        var body = string.IsNullOrWhiteSpace(markdown)
            ? $"# {displayName}\n"
            : markdown.TrimStart();

        return $"---\nname: {EscapeYamlScalar(skillSlug)}\ndescription: {EscapeYamlScalar(description)}\n---\n\n{body}";
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

    private static string StripYamlFrontmatter(string markdown)
    {
        if (!HasYamlFrontmatter(markdown))
            return markdown;

        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        var closingIndex = normalized.IndexOf("\n---\n", StringComparison.Ordinal);
        if (closingIndex < 0)
            return markdown;

        return normalized[(closingIndex + 5)..].TrimStart('\n');
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
        => $"{GetIdentifier()}{SkillsCacheSuffix}";

    private static string CreateSkillVersionId(string normalizedSkillId, string version)
        => $"{normalizedSkillId}:{version}";

    private static bool IsSupportedVersion(string requestedVersion, string? upstreamVersion)
    {
        var normalized = requestedVersion.Trim();
        if (string.Equals(normalized, SyntheticLatestVersion, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(upstreamVersion)
            && string.Equals(normalized, upstreamVersion.Trim(), StringComparison.OrdinalIgnoreCase);
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

    private sealed class GrooveDevSkillsResponse
    {
        [JsonPropertyName("skills")]
        public List<GrooveDevSkillCatalogItem>? Skills { get; set; }
    }

    private sealed class GrooveDevSkillCatalogItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public string? UpdatedAt { get; set; }
    }

    private sealed class GrooveDevSkillContentResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
