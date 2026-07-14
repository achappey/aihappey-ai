using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.LelapaAI;

public partial class LelapaAIProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var models = GetIdentifier().GetModels();
        models.AddRange(BuildTranslationCombinationModels());

        return Task.FromResult<IEnumerable<Model>>(models);
    }
}
