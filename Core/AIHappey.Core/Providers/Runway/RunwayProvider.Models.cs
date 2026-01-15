using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Runway;

public partial class RunwayProvider : IModelProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default) =>
        await Task.FromResult<IEnumerable<Model>>(_keyResolver.Resolve(GetIdentifier()) != null ? [new Model()
            {
                OwnedBy = nameof(Runway),
                Name = "gen4_image",
                Type = "image",
                Id = "gen4_image".ToModelId(GetIdentifier())
            }, new Model()
            {
                OwnedBy = nameof(Runway),
                Type = "image",
                Name = "gen4_image_turbo",
                Id = "gen4_image_turbo".ToModelId(GetIdentifier())
            }, new Model()
            {
                OwnedBy = nameof(Runway),
                Type = "image",
                Name = "gemini_2.5_flash",
                Id = "gemini_2.5_flash".ToModelId(GetIdentifier())
            }] : []);
}
