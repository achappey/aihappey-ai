using System.IO.Compression;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AIHappey.Common.Model.Skills;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    private const string SkillsCacheSuffix = ":skills";
    private static readonly TimeSpan SkillsCacheTtl = TimeSpan.FromMinutes(15);
    private const int SkillsCacheJitterMinutes = 5;

    public async Task<IEnumerable<Skill>> ListSkills(CancellationToken cancellationToken = default)
    {
        if (!IsSkillsStorageEnabled())
            return [];

        return await _memoryCache.GetOrCreateAsync(
            GetSkillsCacheKey(),
            async ct => await DiscoverSkillsAsync(ct),
            baseTtl: SkillsCacheTtl,
            jitterMinutes: SkillsCacheJitterMinutes,
            cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<SkillVersion>> ListSkillVersions(string skillId, CancellationToken cancellationToken = default)
    {
        if (!IsSkillsStorageEnabled())
            return [];

        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        return await DiscoverSkillVersionsAsync(StripProviderPrefix(skillId), cancellationToken);
    }

    public async Task<Stream> RetrieveSkillContent(string skillId, CancellationToken cancellationToken = default)
    {
        if (!IsSkillsStorageEnabled())
            throw new NotImplementedException("Azure skills storage is not configured.");

        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var normalizedSkillId = StripProviderPrefix(skillId);
        var latestVersion = await ResolveLatestVersionAsync(normalizedSkillId, cancellationToken)
            ?? throw new FileNotFoundException($"No versions found for skill '{normalizedSkillId}'.");

        return await BuildSkillBundleAsync(normalizedSkillId, latestVersion.Version, cancellationToken);
    }

    public async Task<Stream> RetrieveSkillVersionContent(string skillId, string version, CancellationToken cancellationToken = default)
    {
        if (!IsSkillsStorageEnabled())
            throw new NotImplementedException("Azure skills storage is not configured.");

        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        return await BuildSkillBundleAsync(StripProviderPrefix(skillId), version.Trim(), cancellationToken);
    }

    private async Task<IEnumerable<Skill>> DiscoverSkillsAsync(CancellationToken cancellationToken)
    {
        var inventory = await DiscoverInventoryAsync(cancellationToken);
        var skills = new List<Skill>(inventory.Count);

        foreach (var skillEntry in inventory.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            var versions = skillEntry.Value.Versions.Values.ToList();
            if (versions.Count == 0)
                continue;

            versions.Sort(CompareVersionRecords);
            var latestVersion = versions[^1];
            var createdAt = versions
                .Select(version => version.CreatedAt)
                .Where(timestamp => timestamp.HasValue)
                .Min();

            var metadata = await ReadSkillMetadataAsync(skillEntry.Key, latestVersion, cancellationToken);

            skills.Add(new Skill
            {
                Id = skillEntry.Key,
                Object = "skill",
                CreatedAt = createdAt,
                DefaultVersion = latestVersion.Version,
                LatestVersion = latestVersion.Version,
                Description = metadata.Description,
                Name = string.IsNullOrWhiteSpace(metadata.Name) ? skillEntry.Key : metadata.Name
            });
        }

        return skills;
    }

    private async Task<IEnumerable<SkillVersion>> DiscoverSkillVersionsAsync(string skillId, CancellationToken cancellationToken)
    {
        var inventory = await DiscoverInventoryAsync(skillId, cancellationToken);
        if (!inventory.TryGetValue(skillId, out var skillRecord))
            return [];

        var versions = skillRecord.Versions.Values.ToList();
        versions.Sort(CompareVersionRecords);

        var result = new List<SkillVersion>(versions.Count);
        foreach (var version in versions)
        {
            var metadata = await ReadSkillMetadataAsync(skillId, version, cancellationToken);

            result.Add(new SkillVersion
            {
                Id = CreateSkillVersionId(skillId, version.Version),
                Object = "skill.version",
                CreatedAt = version.CreatedAt,
                Description = metadata.Description,
                Name = string.IsNullOrWhiteSpace(metadata.Name) ? skillId : metadata.Name,
                SkillId = skillId,
                Version = version.Version
            });
        }

        return result;
    }

    private async Task<MemoryStream> BuildSkillBundleAsync(string skillId, string version, CancellationToken cancellationToken)
    {
        var normalizedSkillId = skillId.Trim();
        var normalizedVersion = version.Trim();
        var versionPrefix = BuildVersionPrefix(normalizedSkillId, normalizedVersion);

        var blobItems = new List<BlobItem>();
        await foreach (var blobItem in GetSkillsContainerClient().GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
                prefix: versionPrefix, cancellationToken: cancellationToken))
            blobItems.Add(blobItem);

        if (blobItems.Count == 0)
            throw new FileNotFoundException($"Skill '{normalizedSkillId}' version '{normalizedVersion}' was not found.");

        if (!blobItems.Any(blob => string.Equals(GetRelativeBundlePath(versionPrefix, blob.Name), "SKILL.md", StringComparison.OrdinalIgnoreCase)))
            throw new FileNotFoundException($"Skill '{normalizedSkillId}' version '{normalizedVersion}' does not contain SKILL.md.");

        var bundleStream = new MemoryStream();
        using (var archive = new ZipArchive(bundleStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var blobItem in blobItems.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = GetRelativeBundlePath(versionPrefix, blobItem.Name);
                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;

                var entry = archive.CreateEntry($"{normalizedSkillId}/{relativePath}", CompressionLevel.Optimal);
                var blobClient = GetSkillsContainerClient().GetBlobClient(blobItem.Name);

                using var zipEntryStream = entry.Open();
                var download = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
                await using var sourceStream = download.Value.Content;
                await sourceStream.CopyToAsync(zipEntryStream, cancellationToken);
            }
        }

        bundleStream.Position = 0;
        return bundleStream;
    }

    private async Task<SkillVersion?> ResolveLatestVersionAsync(string skillId, CancellationToken cancellationToken)
    {
        var versions = (await DiscoverSkillVersionsAsync(skillId, cancellationToken)).ToList();
        if (versions.Count == 0)
            return null;

        versions.Sort(CompareSkillVersions);
        return versions[^1];
    }

    private async Task<Dictionary<string, SkillRecord>> DiscoverInventoryAsync(CancellationToken cancellationToken)
        => await DiscoverInventoryAsync(skillId: null, cancellationToken);

    private async Task<Dictionary<string, SkillRecord>> DiscoverInventoryAsync(string? skillId, CancellationToken cancellationToken)
    {
        var prefix = string.IsNullOrWhiteSpace(skillId)
            ? null
            : $"{skillId.Trim().Trim('/')}/";

        var inventory = new Dictionary<string, SkillRecord>(StringComparer.OrdinalIgnoreCase);

        await foreach (var blobItem in GetSkillsContainerClient().GetBlobsAsync(
                traits: BlobTraits.None,
                states: BlobStates.None,
                prefix: prefix, cancellationToken: cancellationToken))
        {
            var parts = blobItem.Name.Split('/', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
                continue;

            var skillName = parts[0];
            var version = parts[1];
            var relativePath = parts[2];

            if (string.IsNullOrWhiteSpace(skillName) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(relativePath))
                continue;

            if (!inventory.TryGetValue(skillName, out var skillRecord))
            {
                skillRecord = new SkillRecord();
                inventory[skillName] = skillRecord;
            }

            if (!skillRecord.Versions.TryGetValue(version, out var versionRecord))
            {
                versionRecord = new SkillVersionRecord(version);
                skillRecord.Versions[version] = versionRecord;
            }

            versionRecord.BlobNames.Add(blobItem.Name);

            var timestamp = blobItem.Properties.LastModified?.ToUnixTimeSeconds();
            if (timestamp.HasValue && (!versionRecord.CreatedAt.HasValue || timestamp.Value < versionRecord.CreatedAt.Value))
                versionRecord.CreatedAt = timestamp.Value;

            if (string.Equals(relativePath, "SKILL.md", StringComparison.OrdinalIgnoreCase))
                versionRecord.SkillMarkdownBlobName = blobItem.Name;
        }

        return inventory;
    }

    private async Task<SkillMetadata> ReadSkillMetadataAsync(
        string skillId,
        SkillVersionRecord version,
        CancellationToken cancellationToken)
    {
        if (version.Metadata != null)
            return version.Metadata;

        if (string.IsNullOrWhiteSpace(version.SkillMarkdownBlobName))
        {
            version.Metadata = new SkillMetadata(skillId, null);
            return version.Metadata;
        }

        try
        {
            var blobClient = GetSkillsContainerClient().GetBlobClient(version.SkillMarkdownBlobName);
            var response = await blobClient.DownloadContentAsync(cancellationToken: cancellationToken);
            version.Metadata = ParseSkillMarkdown(response.Value.Content.ToString(), skillId);
            return version.Metadata;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            version.Metadata = new SkillMetadata(skillId, null);
            return version.Metadata;
        }
    }

    private static SkillMetadata ParseSkillMarkdown(string markdown, string fallbackSkillName)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new SkillMetadata(fallbackSkillName, null);

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
            return new SkillMetadata(fallbackSkillName, null);

        string? name = null;
        string? description = null;

        for (var index = 1; index < lines.Length; index++)
        {
            var rawLine = lines[index];
            var trimmedLine = rawLine.Trim();

            if (string.Equals(trimmedLine, "---", StringComparison.Ordinal))
                break;

            if (string.IsNullOrWhiteSpace(trimmedLine) || rawLine.StartsWith(' ') || rawLine.StartsWith('\t'))
                continue;

            var separatorIndex = rawLine.IndexOf(':');
            if (separatorIndex <= 0)
                continue;

            var key = rawLine[..separatorIndex].Trim();
            var value = rawLine[(separatorIndex + 1)..].Trim();

            if (value is "|" or ">")
            {
                var blockLines = new List<string>();
                for (index += 1; index < lines.Length; index++)
                {
                    var blockLine = lines[index];
                    if (string.Equals(blockLine.Trim(), "---", StringComparison.Ordinal))
                    {
                        index -= 1;
                        break;
                    }

                    if (!blockLine.StartsWith(' ') && !blockLine.StartsWith('\t'))
                    {
                        index -= 1;
                        break;
                    }

                    blockLines.Add(blockLine.Trim());
                }

                value = value == ">"
                    ? string.Join(" ", blockLines.Where(line => !string.IsNullOrWhiteSpace(line)))
                    : string.Join("\n", blockLines);
            }

            value = UnquoteYamlValue(value);

            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                name = value;
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                description = value;
        }

        return new SkillMetadata(
            string.IsNullOrWhiteSpace(name) ? fallbackSkillName : name,
            string.IsNullOrWhiteSpace(description) ? null : description);
    }

    private static string UnquoteYamlValue(string value)
    {
        if (value.Length >= 2)
        {
            if ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"'))
                return value[1..^1];
        }

        return value;
    }

    private string GetSkillsCacheKey()
        => this.GetCacheKey(_keyResolver.Resolve(GetIdentifier())) + SkillsCacheSuffix;

    private bool IsSkillsStorageEnabled()
        => _skillsContainerClient is not null;

    private BlobContainerClient GetSkillsContainerClient()
        => _skillsContainerClient ?? throw new NotImplementedException("Azure skills storage is not configured.");

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

    private static BlobContainerClient? CreateSkillsContainerClient(AzureSkillsStorageOptions? options)
    {
        if (options == null || string.IsNullOrWhiteSpace(options.ConnectionString))
            return null;

        if (string.IsNullOrWhiteSpace(options.BlobContainerName))
            throw new InvalidOperationException("Azure skills blob container name is required when skills storage is configured.");

        return new BlobContainerClient(options.ConnectionString, options.BlobContainerName);
    }

    private static string BuildVersionPrefix(string skillId, string version)
        => $"{skillId.Trim().Trim('/')}/{version.Trim().Trim('/')}/";

    private static string GetRelativeBundlePath(string versionPrefix, string blobName)
        => blobName.StartsWith(versionPrefix, StringComparison.OrdinalIgnoreCase)
            ? blobName[versionPrefix.Length..]
            : blobName;

    private static string CreateSkillVersionId(string skillId, string version)
        => $"{skillId}:{version}";

    private static int CompareVersionRecords(SkillVersionRecord left, SkillVersionRecord right)
        => CompareVersionNumbers(left.Version, right.Version);

    private static int CompareSkillVersions(SkillVersion? left, SkillVersion? right)
    {
        var versionComparison = CompareVersionNumbers(left?.Version, right?.Version);
        if (versionComparison != 0)
            return versionComparison;

        var createdAtComparison = Nullable.Compare(left?.CreatedAt, right?.CreatedAt);
        if (createdAtComparison != 0)
            return createdAtComparison;

        return StringComparer.Ordinal.Compare(left?.Id, right?.Id);
    }

    private static int CompareVersionNumbers(string? left, string? right)
    {
        var leftParts = ParseVersionParts(left);
        var rightParts = ParseVersionParts(right);

        if (leftParts != null && rightParts != null)
        {
            var maxLength = Math.Max(leftParts.Length, rightParts.Length);
            for (var index = 0; index < maxLength; index++)
            {
                var leftPart = index < leftParts.Length ? leftParts[index] : 0;
                var rightPart = index < rightParts.Length ? rightParts[index] : 0;
                var partComparison = leftPart.CompareTo(rightPart);
                if (partComparison != 0)
                    return partComparison;
            }

            return 0;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left ?? string.Empty, right ?? string.Empty);
    }

    private static int[]? ParseVersionParts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return [];

        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var values = new int[parts.Length];

        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out values[index]))
                return null;
        }

        return values;
    }

    private sealed class SkillRecord
    {
        public Dictionary<string, SkillVersionRecord> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SkillVersionRecord(string version)
    {
        public string Version { get; } = version;

        public long? CreatedAt { get; set; }

        public string? SkillMarkdownBlobName { get; set; }

        public List<string> BlobNames { get; } = [];

        public SkillMetadata? Metadata { get; set; }
    }

    private sealed record SkillMetadata(string Name, string? Description);
}
