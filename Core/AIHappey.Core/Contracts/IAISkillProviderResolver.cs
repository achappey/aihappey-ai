using AIHappey.Common.Model.Skills;

namespace AIHappey.Core.Contracts;

public interface IAISkillProviderResolver
{
    ISkillProvider GetProvider();

    Task<DataList<Skill>> ResolveSkills(
        string? after = null,
        int? limit = null,
        string? order = null,
        CancellationToken ct = default);

    Task<DataList<SkillVersion>> ResolveSkillVersions(
        string skillId,
        string? after = null,
        int? limit = null,
        string? order = null,
        CancellationToken ct = default);

    Task<Stream> RetrieveSkillContent(
        string skillId,
        CancellationToken ct);

    Task<Stream> RetrieveSkillVersionContent(
        string skillId,
        string version,
        CancellationToken ct);

}
