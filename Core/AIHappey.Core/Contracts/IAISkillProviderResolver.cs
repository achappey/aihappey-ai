using AIHappey.Common.Model.Skills;
using AIHappey.Core.Models;

namespace AIHappey.Core.Contracts;

public interface IAISkillProviderResolver
{
    ISkillProvider GetProvider();

    Task<SkillList> ResolveSkills(
        string? after = null,
        int? limit = null,
        string? order = null,
        CancellationToken ct = default);

    Task<Stream> RetrieveSkillContent(
        string skillId,
        CancellationToken ct);

}
