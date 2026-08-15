using System.IO.Compression;
using System.Text;
using AIHappey.Core.Contracts;

namespace AIHappey.Core.MCP.Skills;

internal sealed record SkillBundle(
    string SkillId,
    string? Version,
    string Name,
    string? Description,
    string Body,
    IReadOnlyDictionary<string, SkillBundleResource> Resources)
{
    private const long MaximumEntryBytes = 10 * 1024 * 1024;
    private const long MaximumBundleBytes = 50 * 1024 * 1024;

    public static async Task<SkillBundle> LoadAsync(
        IAISkillProviderResolver resolver,
        string skillId,
        string? version,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        await using var source = string.IsNullOrWhiteSpace(version)
            ? await resolver.RetrieveSkillContent(skillId.Trim(), cancellationToken)
            : await resolver.RetrieveSkillVersionContent(skillId.Trim(), version.Trim(), cancellationToken);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);

        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (files.Length == 0)
            throw new InvalidDataException($"Skill '{skillId}' returned an empty bundle.");

        var manifestEntries = files.Where(entry => string.Equals(entry.Name, "SKILL.md", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifestEntries.Length != 1)
            throw new InvalidDataException($"Skill '{skillId}' bundle must contain exactly one SKILL.md file.");

        var root = GetDirectory(manifestEntries[0].FullName);
        var resources = new Dictionary<string, SkillBundleResource>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        foreach (var entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = NormalizeArchivePath(entry.FullName);
            if (!IsUnderRoot(fullPath, root))
                throw new InvalidDataException($"Skill '{skillId}' bundle contains a file outside its skill root.");

            var relativePath = root.Length == 0 ? fullPath : fullPath[(root.Length + 1)..];
            relativePath = NormalizeRelativePath(relativePath);
            if (entry.Length > MaximumEntryBytes || totalBytes + entry.Length > MaximumBundleBytes)
                throw new InvalidDataException($"Skill '{skillId}' bundle exceeds the supported size limit.");

            await using var entryStream = entry.Open();
            using var buffer = new MemoryStream((int)entry.Length);
            await entryStream.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            totalBytes += bytes.Length;

            if (!string.Equals(relativePath, "SKILL.md", StringComparison.OrdinalIgnoreCase))
                resources.Add(relativePath, new SkillBundleResource(bytes, ResolveMimeType(relativePath)));
        }

        var manifest = await ReadEntryTextAsync(manifestEntries[0], cancellationToken);
        var parsed = ParseManifest(manifest, skillId);
        return new SkillBundle(skillId.Trim(), string.IsNullOrWhiteSpace(version) ? null : version.Trim(), parsed.Name, parsed.Description, parsed.Body, resources);
    }

    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A relative skill resource path is required.", nameof(path));

        var normalized = path.Replace('\\', '/').Trim().TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
            throw new InvalidDataException("Skill resource path must remain inside the skill root.");

        return string.Join('/', segments);
    }

    private static string NormalizeArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
            throw new InvalidDataException("Skill bundle contains an unsafe archive path.");

        return string.Join('/', segments);
    }

    private static bool IsUnderRoot(string path, string root)
        => root.Length == 0 || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private static string GetDirectory(string path)
    {
        var normalized = NormalizeArchivePath(path);
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    private static async Task<string> ReadEntryTextAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length > MaximumEntryBytes)
            throw new InvalidDataException("SKILL.md exceeds the supported size limit.");

        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static (string Name, string? Description, string Body) ParseManifest(string markdown, string fallbackName)
    {
        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return (fallbackName.Split('/').Last(), null, normalized.Trim());

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidDataException("SKILL.md contains unterminated YAML frontmatter.");

        string? name = null;
        string? description = null;
        foreach (var line in normalized[4..end].Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase)) name = value;
            if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase)) description = value;
        }

        return (string.IsNullOrWhiteSpace(name) ? fallbackName.Split('/').Last() : name, description, normalized[(end + 5)..].Trim());
    }

    private static string ResolveMimeType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".md" => "text/markdown",
            ".txt" or ".log" => "text/plain",
            ".json" => "application/json",
            ".yaml" or ".yml" => "application/yaml",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" or ".mjs" => "text/javascript",
            ".ts" or ".tsx" => "text/typescript",
            ".cs" => "text/x-csharp",
            ".py" => "text/x-python",
            ".sh" => "text/x-shellscript",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
}

internal sealed record SkillBundleResource(byte[] Bytes, string MimeType)
{
    public bool IsText => MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || MimeType is "application/json" or "application/yaml" or "application/xml" or "image/svg+xml";

    public string ReadText() => Encoding.UTF8.GetString(Bytes);
}
