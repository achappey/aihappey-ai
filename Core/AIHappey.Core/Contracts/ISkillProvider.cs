using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Common.Model.Skills;

namespace AIHappey.Core.Contracts;

public interface ISkillProvider
{
    string GetIdentifier();

    Task<IEnumerable<Skill>> ListSkills(CancellationToken cancellationToken = default);

    Task<Stream> RetrieveSkillContent(string skillId, CancellationToken cancellationToken = default);

}
