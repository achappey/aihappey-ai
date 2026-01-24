using AIHappey.Core.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var googleAI = GetClient();
        var generativeModel = googleAI.GenerativeModel();
        var models = await generativeModel.ListModels(pageSize: 1000);

        string[] excludedSubstrings = [
            "embedding",
            "native",
        ];

        return models
            .Select(a =>
            {
                var id = a.Name?.Split("/").LastOrDefault() ?? string.Empty;

                GoogleAIModels.ModelCreatedAt.TryGetValue(id, out var createdAt);

                return new Model()
                {
                    Name = a.DisplayName!,
                    OwnedBy = Google,
                    Id = id.ToModelId(GetIdentifier()),
                    Created = createdAt != default ? createdAt.ToUnixTimeSeconds() : null
                };
            })
            .Where(a => excludedSubstrings.All(z => a.Id?.Contains(z) != true));
    }
}
