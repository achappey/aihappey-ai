using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Skills;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.TerminalSkills;

public partial class TerminalSkillsProvider
{
    private const string SkillsCacheSuffix = ":skills";
    private static readonly TimeSpan SkillsCacheTtl = TimeSpan.FromMinutes(15);
    private const int SkillsCacheJitterMinutes = 5;
    private const int SkillsPageLimit = 100;

    public async Task<IEnumerable<Skill>> ListSkills(CancellationToken cancellationToken = default)
    {
        return await _memoryCache.GetOrCreateAsync(
            GetSkillsCacheKey(),
            FetchSkillsFromTerminalSkills,
            baseTtl: SkillsCacheTtl,
            jitterMinutes: SkillsCacheJitterMinutes,
            cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<SkillVersion>> ListSkillVersions(string skillId, CancellationToken cancellationToken = default)
    {
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
                CreatedAt = null,
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

    private async Task<IEnumerable<Skill>> FetchSkillsFromTerminalSkills(CancellationToken cancellationToken)
    {
        var allSkills = new List<Skill>();
        var page = 1;
        bool hasMore;

        do
        {
            var response = await SendSkillsRequestAsync<TerminalSkillsListResponse>(BuildSkillsListUri(page), cancellationToken);

            if (response?.Data != null)
                allSkills.AddRange(response.Data.Select(MapSkill));

            hasMore = response?.Pagination?.HasMore == true;
            page++;
        }
        while (hasMore);

        return [.. allSkills
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Id))
            .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private static string BuildSkillsListUri(int page)
        => $"v1/skills?page={page.ToString(CultureInfo.InvariantCulture)}&limit={SkillsPageLimit.ToString(CultureInfo.InvariantCulture)}";

    private async Task<TerminalSkillDetail> GetSkillDetailsAsync(string slug, CancellationToken cancellationToken)
    {
        var response = await SendSkillsRequestAsync<TerminalSkillDetailResponse>(
            $"v1/skills/{Uri.EscapeDataString(slug)}",
            cancellationToken);

        return response?.Data ?? throw new FileNotFoundException($"Skill '{slug}' was not found.");
    }

    private async Task<T?> SendSkillsRequestAsync<T>(string requestUri, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return default;

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Terminal Skills API error ({(int)response.StatusCode}): {error}");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private Skill MapSkill(TerminalSkill item)
    {
        var slug = item.Slug ?? string.Empty;
        var normalizedSkillId = EnsureProviderPrefixedSkillId(slug);
        var version = NormalizeVersion(item.Version);

        return new Skill
        {
            Id = normalizedSkillId,
            Object = "skill",
            CreatedAt = null,
            DefaultVersion = version,
            LatestVersion = version,
            Description = item.Description,
            Name = string.IsNullOrWhiteSpace(item.Name) ? slug : item.Name
        };
    }

    private Stream BuildSkillBundle(TerminalSkillDetail detail, string fallbackSlug)
    {
        var slug = !string.IsNullOrWhiteSpace(detail.Slug)
            ? detail.Slug.Trim()
            : fallbackSlug.Trim();
        var specName = ToSpecName(slug);
        var markdown = BuildAgentSkillMarkdown(detail, specName);

        var bundleStream = new MemoryStream();
        using (var archive = new ZipArchive(bundleStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry($"{specName}/SKILL.md", CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(markdown);
        }

        bundleStream.Position = 0;
        return bundleStream;
    }

    private static string BuildAgentSkillMarkdown(TerminalSkillDetail detail, string specName)
    {
        var sourceMarkdown = detail.BodyMarkdown ?? string.Empty;
        var body = ExtractMarkdownBody(sourceMarkdown);
        if (string.IsNullOrWhiteSpace(body))
            body = $"# {detail.Name ?? specName}\n";

        var description = NormalizeDescription(detail.Description);
        var compatibility = NormalizeOptionalScalar(detail.Compatibility, maxLength: 500);
        var license = NormalizeOptionalScalar(detail.License, maxLength: 200);

        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.Append("name: ").AppendLine(EscapeYamlScalar(specName));
        builder.Append("description: ").AppendLine(EscapeYamlScalar(description));

        if (!string.IsNullOrWhiteSpace(license))
            builder.Append("license: ").AppendLine(EscapeYamlScalar(license));

        if (!string.IsNullOrWhiteSpace(compatibility))
            builder.Append("compatibility: ").AppendLine(EscapeYamlScalar(compatibility));

        AppendMetadata(builder, detail);
        builder.AppendLine("---");
        builder.AppendLine();
        builder.Append(body.TrimStart());

        if (!builder.ToString().EndsWith('\n'))
            builder.AppendLine();

        return builder.ToString();
    }

    private static void AppendMetadata(StringBuilder builder, TerminalSkillDetail detail)
    {
        var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["terminalskills-slug"] = detail.Slug ?? string.Empty
        };

        AddMetadataValue(metadata, "terminalskills-author", detail.Author);
        AddMetadataValue(metadata, "terminalskills-version", detail.Version);
        AddMetadataValue(metadata, "terminalskills-category", detail.Category);

        if (detail.Downloads.HasValue)
            metadata["terminalskills-downloads"] = detail.Downloads.Value.ToString(CultureInfo.InvariantCulture);

        if (detail.Stars.HasValue)
            metadata["terminalskills-stars"] = detail.Stars.Value.ToString(CultureInfo.InvariantCulture);

        AddMetadataValues(metadata, "terminalskills-tags", detail.Tags);
        AddMetadataValues(metadata, "terminalskills-supported-agents", detail.SupportedAgents);
        AddMetadataValues(metadata, "terminalskills-use-cases", detail.UseCases);

        var filtered = metadata
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToArray();
        if (filtered.Length == 0)
            return;

        builder.AppendLine("metadata:");
        foreach (var pair in filtered)
            builder.Append("  ").Append(pair.Key).Append(": ").AppendLine(EscapeYamlScalar(pair.Value));
    }

    private static void AddMetadataValue(SortedDictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            metadata[key] = value.Trim();
    }

    private static void AddMetadataValues(SortedDictionary<string, string> metadata, string key, IEnumerable<string>? values)
    {
        var normalized = values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized is { Length: > 0 })
            metadata[key] = string.Join(", ", normalized);
    }

    private static string ExtractMarkdownBody(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return markdown.TrimStart();

        var closingIndex = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (closingIndex < 0)
            return markdown.TrimStart();

        return normalized[(closingIndex + "\n---\n".Length)..].TrimStart();
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
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        return string.IsNullOrWhiteSpace(key)
            ? $"skills:{GetIdentifier()}{SkillsCacheSuffix}"
            : $"skills:{GetIdentifier()}:{ModelProviderExtensions.CacheKeyFromApiKey(key)}{SkillsCacheSuffix}";
    }

    private static string CreateSkillVersionId(string normalizedSkillId, string version)
        => $"{normalizedSkillId}:{version}";

    private static string NormalizeVersion(string? version)
    {
        var normalized = version?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "1.0.0" : normalized;
    }

    private static string NormalizeDescription(string? description)
    {
        var normalized = NormalizeOptionalScalar(description, maxLength: 1024);
        return string.IsNullOrWhiteSpace(normalized)
            ? "Terminal Skills agent skill. Use when the user asks for help with this workflow."
            : normalized;
    }

    private static string? NormalizeOptionalScalar(string? value, int maxLength)
    {
        var normalized = value?.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Trim();

        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].TrimEnd();
    }

    private static string ToSpecName(string? slug)
    {
        var source = string.IsNullOrWhiteSpace(slug) ? "skill" : slug.Trim().ToLowerInvariant();
        var builder = new StringBuilder(source.Length);
        var previousWasHyphen = false;

        foreach (var ch in source)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
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
        while (result.Contains("--", StringComparison.Ordinal))
            result = result.Replace("--", "-", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(result))
            return "skill";

        return result.Length <= 64 ? result : result[..64].TrimEnd('-');
    }

    private static string EscapeYamlScalar(string value)
        => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private sealed class TerminalSkillsListResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public List<TerminalSkill>? Data { get; set; }

        [JsonPropertyName("pagination")]
        public TerminalSkillsPagination? Pagination { get; set; }
    }

    private sealed class TerminalSkillDetailResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public TerminalSkillDetail? Data { get; set; }
    }

    private class TerminalSkill
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("downloads")]
        public int? Downloads { get; set; }

        [JsonPropertyName("stars")]
        public int? Stars { get; set; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }

        [JsonPropertyName("license")]
        public string? License { get; set; }

        [JsonPropertyName("compatibility")]
        public string? Compatibility { get; set; }

        [JsonPropertyName("supportedAgents")]
        public List<string>? SupportedAgents { get; set; }

        [JsonPropertyName("useCases")]
        public List<string>? UseCases { get; set; }
    }

    private sealed class TerminalSkillDetail : TerminalSkill
    {
        [JsonPropertyName("bodyMarkdown")]
        public string? BodyMarkdown { get; set; }
    }

    private sealed class TerminalSkillsPagination
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        [JsonPropertyName("hasMore")]
        public bool HasMore { get; set; }
    }
}
