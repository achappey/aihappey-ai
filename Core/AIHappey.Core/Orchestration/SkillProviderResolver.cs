using AIHappey.Common.Model.Skills;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;

namespace AIHappey.Core.Orchestration;

public class SkillProviderResolver(
    IApiKeyResolver apiKeyResolver,
    IEnumerable<ISkillProvider> providers) : IAISkillProviderResolver
{
    private readonly ISkillProvider[] _providers = providers as ISkillProvider[] ?? [.. providers];

    public async Task<ISkillProvider> Resolve(string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model))
            return GetProvider();

        var provider = FindProviderByIdentifier(model);
        if (provider != null)
            return provider;

        if (model.Contains('/', StringComparison.Ordinal))
        {
            var split = model.SplitModelId();
            provider = FindProviderByIdentifier(split.Provider);
            if (provider != null)
                return provider;
        }

        var map = await GetAggregateMapAsync(ct);

        if (map.TryGetValue(model, out var entry))
            return entry.Provider;

        var key = map.Keys.FirstOrDefault(z => z.SplitModelId().Model == model);
        if (key != null && map.TryGetValue(key, out var fallbackEntry))
            return fallbackEntry.Provider;

        throw new NotSupportedException($"No skill provider found for '{model}'.");
    }

    public ISkillProvider GetProvider()
        => GetConfiguredProviders().FirstOrDefault()
           ?? _providers.FirstOrDefault()
           ?? throw new NotSupportedException("No skill providers found.");

    public async Task<DataList<Skill>> ResolveSkills(
        string? after = null,
        int? limit = null,
        string? order = null,
        CancellationToken ct = default)
    {
        var map = await GetAggregateMapAsync(ct);
        var orderedSkills = ApplyOrdering(map.Values.Select(v => v.Skill), order).ToList();

        if (!string.IsNullOrWhiteSpace(after))
        {
            var afterIndex = orderedSkills.FindIndex(skill =>
                string.Equals(skill.Id, after, StringComparison.Ordinal)
                || (skill.Id.Contains('/', StringComparison.Ordinal)
                    && string.Equals(skill.Id.SplitModelId().Model, after, StringComparison.Ordinal)));

            if (afterIndex >= 0)
                orderedSkills = [.. orderedSkills.Skip(afterIndex + 1)];
        }

        var pageSize = limit.GetValueOrDefault(orderedSkills.Count);
        if (pageSize <= 0)
            pageSize = orderedSkills.Count;

        var page = orderedSkills.Take(pageSize).ToList();

        return new DataList<Skill>
        {
            Object = "list",
            Data = page,
            FirstId = page.FirstOrDefault()?.Id ?? string.Empty,
            LastId = page.LastOrDefault()?.Id ?? string.Empty,
            HasMore = orderedSkills.Count > page.Count
        };
    }

    public async Task<Stream> RetrieveSkillContent(string skillId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var provider = await Resolve(skillId, ct);
        return await provider.RetrieveSkillContent(skillId, ct);
    }

    public async Task<Stream> RetrieveSkillVersionContent(string skillId, string version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var provider = await Resolve(skillId, ct);
        return await provider.RetrieveSkillVersionContent(skillId, version, ct);
    }

    private async Task<Dictionary<string, (Skill Skill, ISkillProvider Provider)>> GetAggregateMapAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, (Skill Skill, ISkillProvider Provider)>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in GetAggregateProviders())
        {
            var providerId = provider.GetIdentifier();
            var skills = await provider.ListSkills(ct);

            foreach (var skill in skills.Select(skill => NormalizeSkill(skill, providerId)))
                map[skill.Id] = (skill, provider);
        }

        return map;
    }

    private ISkillProvider? FindProviderByIdentifier(string providerId)
        => GetAggregateProviders().FirstOrDefault(p => string.Equals(p.GetIdentifier(), providerId, StringComparison.OrdinalIgnoreCase))
           ?? _providers.FirstOrDefault(p => string.Equals(p.GetIdentifier(), providerId, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<ISkillProvider> GetConfiguredProviders()
        => _providers.Where(HasConfiguredApiKey);

    private IEnumerable<ISkillProvider> GetAggregateProviders()
    {
        var configured = GetConfiguredProviders().ToArray();
        return configured.Length > 0 ? configured : _providers;
    }

    private bool HasConfiguredApiKey(ISkillProvider provider)
        => !string.IsNullOrWhiteSpace(apiKeyResolver.Resolve(provider.GetIdentifier()));

    private static Skill NormalizeSkill(Skill skill, string providerId)
    {
        var id = skill.Id ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
            return skill;

        var normalizedId = id;
        if (!id.Contains('/', StringComparison.Ordinal))
        {
            normalizedId = id.ToModelId(providerId);
        }
        else
        {
            var split = id.SplitModelId();
            if (!string.Equals(split.Provider, providerId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(split.Model))
            {
                normalizedId = id.ToModelId(providerId);
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

    private static IEnumerable<Skill> ApplyOrdering(IEnumerable<Skill> skills, string? order)
    {
        var normalizedOrder = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)
            ? "asc"
            : "desc";

        return normalizedOrder == "asc"
            ? skills.OrderBy(skill => skill.CreatedAt ?? long.MinValue).ThenBy(skill => skill.Id, StringComparer.Ordinal)
            : skills.OrderByDescending(skill => skill.CreatedAt ?? long.MinValue).ThenBy(skill => skill.Id, StringComparer.Ordinal);
    }

    public async Task<DataList<SkillVersion>> ResolveSkillVersions(
        string skillId,
        string? after = null,
        int? limit = null,
        string? order = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var provider = await Resolve(skillId, ct);
        var orderedVersions = ApplyVersionOrdering(
                (await provider.ListSkillVersions(skillId, ct)).Select(version => NormalizeSkillVersion(version, provider.GetIdentifier(), skillId)),
                order)
            .ToList();

        if (!string.IsNullOrWhiteSpace(after))
        {
            var afterIndex = orderedVersions.FindIndex(version =>
                string.Equals(version.Id, after, StringComparison.Ordinal));

            if (afterIndex >= 0)
                orderedVersions = [.. orderedVersions.Skip(afterIndex + 1)];
        }

        var pageSize = limit.GetValueOrDefault(orderedVersions.Count);
        if (pageSize <= 0)
            pageSize = orderedVersions.Count;

        var page = orderedVersions.Take(pageSize).ToList();

        return new DataList<SkillVersion>
        {
            Object = "list",
            Data = page,
            FirstId = page.FirstOrDefault()?.Id ?? string.Empty,
            LastId = page.LastOrDefault()?.Id ?? string.Empty,
            HasMore = orderedVersions.Count > page.Count
        };
    }

    private static SkillVersion NormalizeSkillVersion(SkillVersion version, string providerId, string fallbackSkillId)
    {
        var normalizedSkillId = fallbackSkillId;
        var skillId = version.SkillId ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(skillId))
        {
            if (!skillId.Contains('/', StringComparison.Ordinal))
            {
                normalizedSkillId = skillId.ToModelId(providerId);
            }
            else
            {
                var split = skillId.SplitModelId();
                normalizedSkillId = string.Equals(split.Provider, providerId, StringComparison.OrdinalIgnoreCase)
                                   && !string.IsNullOrWhiteSpace(split.Model)
                    ? skillId
                    : skillId.ToModelId(providerId);
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

    private static IEnumerable<SkillVersion> ApplyVersionOrdering(IEnumerable<SkillVersion> versions, string? order)
    {
        var normalizedOrder = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)
            ? "asc"
            : "desc";

        var orderedVersions = versions.ToList();
        orderedVersions.Sort(CompareSkillVersions);

        if (normalizedOrder == "desc")
            orderedVersions.Reverse();

        return orderedVersions;
    }

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

        return StringComparer.Ordinal.Compare(left ?? string.Empty, right ?? string.Empty);
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
}
