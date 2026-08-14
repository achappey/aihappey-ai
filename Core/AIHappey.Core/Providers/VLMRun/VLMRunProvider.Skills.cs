using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    private static readonly Regex AgentSkillNameRegex = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        await using var upstreamBundleStream = new MemoryStream();
        await sourceStream.CopyToAsync(upstreamBundleStream, cancellationToken);
        upstreamBundleStream.Position = 0;

        return await NormalizeSkillBundleAsync(upstreamBundleStream, skillId, cancellationToken);
    }

    private static async Task<MemoryStream> NormalizeSkillBundleAsync(
        Stream upstreamBundle,
        string skillId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var sourceArchive = new ZipArchive(upstreamBundle, ZipArchiveMode.Read, leaveOpen: true);
            var sourceFiles = sourceArchive.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .Select(entry => new SkillBundleEntry(entry, NormalizeSkillArchivePath(entry.FullName, skillId)))
                .ToArray();

            if (sourceFiles.Length == 0)
                throw InvalidSkillBundle(skillId, "the ZIP archive is empty");

            var duplicatePath = sourceFiles
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicatePath is not null)
                throw InvalidSkillBundle(skillId, $"the ZIP archive contains duplicate path '{duplicatePath.Key}'");

            var manifestFiles = sourceFiles
                .Where(item => string.Equals(Path.GetFileName(item.Path), "SKILL.md", StringComparison.Ordinal))
                .ToArray();
            if (manifestFiles.Length != 1)
                throw InvalidSkillBundle(skillId, "the ZIP archive must contain exactly one file named SKILL.md");

            var manifest = manifestFiles[0];
            var manifestSegments = manifest.Path.Split('/', StringSplitOptions.None);
            if (manifestSegments.Length is not (1 or 2))
                throw InvalidSkillBundle(skillId, "SKILL.md must be at the archive root or directly inside one root folder");

            var sourceRoot = manifestSegments.Length == 2 ? manifestSegments[0] : null;
            if (sourceRoot is not null && sourceFiles.Any(item => !item.Path.StartsWith($"{sourceRoot}/", StringComparison.Ordinal)))
                throw InvalidSkillBundle(skillId, "the ZIP archive contains files outside the folder that contains SKILL.md");

            var markdown = await ReadSkillMarkdownAsync(manifest.Entry, skillId, cancellationToken);
            var frontmatter = ParseAndValidateSkillFrontmatter(markdown, skillId);
            var outputPaths = sourceFiles.Select(item => new
            {
                Source = item,
                RelativePath = sourceRoot is null ? item.Path : item.Path[(sourceRoot.Length + 1)..]
            }).Select(item => new
            {
                item.Source,
                OutputPath = $"{frontmatter.Name}/{item.RelativePath}"
            }).ToArray();

            var duplicateOutputPath = outputPaths
                .GroupBy(item => item.OutputPath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateOutputPath is not null)
                throw InvalidSkillBundle(skillId, $"normalization would create duplicate path '{duplicateOutputPath.Key}'");

            var normalizedBundle = new MemoryStream();
            using (var outputArchive = new ZipArchive(normalizedBundle, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var item in outputPaths.OrderBy(item => item.OutputPath, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var outputEntry = outputArchive.CreateEntry(item.OutputPath, CompressionLevel.Optimal);
                    await using var input = item.Source.Entry.Open();
                    await using var output = outputEntry.Open();
                    await input.CopyToAsync(output, cancellationToken);
                }
            }

            normalizedBundle.Position = 0;
            return normalizedBundle;
        }
        catch (InvalidDataException exception) when (!exception.Data.Contains(nameof(VLMRunProvider)))
        {
            throw InvalidSkillBundle(skillId, "the downloaded content is not a valid ZIP archive", exception);
        }
    }

    private static string NormalizeSkillArchivePath(string path, string skillId)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw InvalidSkillBundle(skillId, "the ZIP archive contains an empty file path");

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(normalized, "^[A-Za-z]:/"))
            throw InvalidSkillBundle(skillId, $"the ZIP archive contains absolute path '{path}'");

        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw InvalidSkillBundle(skillId, $"the ZIP archive contains unsafe path '{path}'");

        return string.Join('/', segments);
    }

    private static async Task<string> ReadSkillMarkdownAsync(
        ZipArchiveEntry entry,
        string skillId,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);

        try
        {
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidSkillBundle(skillId, "SKILL.md is not valid UTF-8", exception);
        }
    }

    private static SkillBundleFrontmatter ParseAndValidateSkillFrontmatter(string markdown, string skillId)
    {
        var normalized = markdown.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0], "---", StringComparison.Ordinal))
            throw InvalidSkillBundle(skillId, "SKILL.md must start with YAML frontmatter delimited by ---");

        var closingDelimiter = Array.FindIndex(lines, 1, line => string.Equals(line, "---", StringComparison.Ordinal));
        if (closingDelimiter < 0)
            throw InvalidSkillBundle(skillId, "SKILL.md frontmatter is missing its closing --- delimiter");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < closingDelimiter; index++)
        {
            var rawLine = lines[index];
            if (string.IsNullOrWhiteSpace(rawLine) || rawLine.TrimStart().StartsWith('#'))
                continue;

            if (char.IsWhiteSpace(rawLine[0]))
                continue;

            var separator = rawLine.IndexOf(':');
            if (separator <= 0)
                throw InvalidSkillBundle(skillId, $"SKILL.md contains malformed frontmatter line '{rawLine}'");

            var key = rawLine[..separator].Trim();
            var rawValue = rawLine[(separator + 1)..].Trim();
            if (values.ContainsKey(key))
                throw InvalidSkillBundle(skillId, $"SKILL.md frontmatter contains duplicate field '{key}'");

            if (rawValue.StartsWith('|') || rawValue.StartsWith('>'))
            {
                var blockLines = new List<string>();
                while (index + 1 < closingDelimiter &&
                       (string.IsNullOrWhiteSpace(lines[index + 1]) || char.IsWhiteSpace(lines[index + 1][0])))
                {
                    index++;
                    blockLines.Add(lines[index].Trim());
                }

                values[key] = rawValue.StartsWith('>')
                    ? string.Join(' ', blockLines)
                    : string.Join('\n', blockLines);
            }
            else
            {
                values[key] = TrimYamlScalar(rawValue);
            }
        }

        if (!values.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            throw InvalidSkillBundle(skillId, "SKILL.md frontmatter must contain a non-empty name field");
        if (name.Length > 64 || !AgentSkillNameRegex.IsMatch(name))
            throw InvalidSkillBundle(skillId, $"SKILL.md name '{name}' must be 1-64 lowercase letters, numbers, or single hyphen-separated words");

        if (!values.TryGetValue("description", out var description) || string.IsNullOrWhiteSpace(description))
            throw InvalidSkillBundle(skillId, "SKILL.md frontmatter must contain a non-empty description field");
        if (description.Length > 1024)
            throw InvalidSkillBundle(skillId, "SKILL.md description must not exceed 1024 characters");

        if (values.TryGetValue("compatibility", out var compatibility) &&
            (string.IsNullOrWhiteSpace(compatibility) || compatibility.Length > 500))
            throw InvalidSkillBundle(skillId, "SKILL.md compatibility must contain 1-500 characters when provided");

        return new SkillBundleFrontmatter(name);
    }

    private static string TrimYamlScalar(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) ||
             (trimmed.StartsWith('\'') && trimmed.EndsWith('\''))))
            return trimmed[1..^1].Trim();

        return trimmed;
    }

    private static InvalidDataException InvalidSkillBundle(string skillId, string reason, Exception? innerException = null)
    {
        var exception = new InvalidDataException($"VLMRun skill '{skillId}' returned an invalid Agent Skill bundle: {reason}.", innerException);
        exception.Data[nameof(VLMRunProvider)] = true;
        return exception;
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

    private sealed record SkillBundleEntry(ZipArchiveEntry Entry, string Path);

    private sealed record SkillBundleFrontmatter(string Name);
}
