using AIHappey.Common.Model.Skills;

namespace AIHappey.Core.Contracts;

public interface ISkillProvider
{
    string GetIdentifier();

    Task<IEnumerable<Skill>> ListSkills(CancellationToken cancellationToken = default);
    Task<IEnumerable<SkillVersion>> ListSkillVersions(string skillId, CancellationToken cancellationToken = default);

    Task<Stream> RetrieveSkillContent(string skillId, CancellationToken cancellationToken = default);
    Task<Stream> RetrieveSkillVersionContent(string skillId, string version, CancellationToken cancellationToken = default);

}
