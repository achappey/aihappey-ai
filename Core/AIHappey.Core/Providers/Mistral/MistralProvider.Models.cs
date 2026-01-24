using AIHappey.Core.AI;
using AIHappey.Core.Models;
using MIS = Mistral.SDK;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var client = new MIS.MistralClient(
          _keyResolver.Resolve(GetIdentifier()),
          _client
        );

        var models = await client.Models
            .GetModelsAsync(cancellationToken: cancellationToken);

        List<Model> imageModels = [new Model()
            {
                Id = "mistral-medium-latest".ToModelId(GetIdentifier()),
                Name = "mistral-medium-latest",
                OwnedBy = GetName(),
                Type = "image"
            }, new Model()
            {
                Id = "mistral-large-latest".ToModelId(GetIdentifier()),
                Name = "mistral-large-latest",
                OwnedBy = GetName(),
                Type = "image"
            }];

        return models.Data
            .Select(a => new Model()
            {
                Id = a.Id.ToModelId(GetIdentifier()),
                Name = a.Id,
                OwnedBy = GetName(),
            })
            .Concat(imageModels)
            .OrderByDescending(a => a.Created);
    }


}