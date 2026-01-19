using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Verda;

public partial class VerdaProvider
    : IModelProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await Task.FromResult(new List<Model>()
       {
           new()
           {
               Id = "flux-dev".ToModelId(GetIdentifier()),
               Name = "FLUX.1 [dev]",
               Type = "image"
           },
            new()
           {
               Id = "flux-krea-dev".ToModelId(GetIdentifier()),
               Name = "FLUX.1 Krea [dev]",
               Type = "image"
           },
            new()
           {
               Id = "flux-kontext-max".ToModelId(GetIdentifier()),
               Name = "FLUX.1 Kontext [max]",
               Type = "image"
           },
           new()
           {
               Id = "flux-kontext-pro".ToModelId(GetIdentifier()),
               Name = "FLUX.1 Kontext [pro]",
               Type = "image"
           },
           new()
           {
               Id = "flux-kontext-dev".ToModelId(GetIdentifier()),
               Name = "FLUX.1 Kontext [dev]",
               Type = "image"
           },
            new()
           {
               Id = "flux2-dev".ToModelId(GetIdentifier()),
               Name = "FLUX.2 [dev]",
               Type = "image"
           },
            new()
           {
               Id = "flux-2-flex".ToModelId(GetIdentifier()),
               Name = "FLUX.2 [flex]",
               Type = "image"
           },
            new()
           {
               Id = "flux-2-pro".ToModelId(GetIdentifier()),
               Name = "FLUX.2 [pro]",
               Type = "image"
           },
            new()
           {
               Id = "flux2-klein-4b".ToModelId(GetIdentifier()),
               Name = "FLUX.2 [klein] 4B",
               Type = "image"
           },
            new()
           {
               Id = "flux2-klein-base-4b".ToModelId(GetIdentifier()),
               Name = "FLUX.2 [klein] Base 4B",
               Type = "image"
           },
            new()
           {
               Id = "flux2-klein-base-9b".ToModelId(GetIdentifier()),
               Name = "FLUX.2 [klein] Base 9B",
               Type = "image"
           }
       });
    }
}

