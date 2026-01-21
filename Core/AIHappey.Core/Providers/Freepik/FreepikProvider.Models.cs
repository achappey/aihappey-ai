using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        // Hardcoded per user request.
        return Task.FromResult<IEnumerable<Model>>(_keyResolver.Resolve(GetIdentifier()) != null
            ?
            [
                //text-to-icon disabled, required webhook
      /*          new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "text-to-icon",
                    Type = "image",
                    Id = "text-to-icon".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "text-to-icon/preview",
                    Type = "image",
                    Id = "text-to-icon/preview".ToModelId(GetIdentifier())
                },*/
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "skin-enhancer/creative",
                    Type = "image",
                    Id = "skin-enhancer/creative".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "skin-enhancer/faithful",
                    Type = "image",
                    Id = "skin-enhancer/faithful".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "skin-enhancer/flexible",
                    Type = "image",
                    Id = "skin-enhancer/flexible".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "image-expand/flux-pro",
                    Type = "image",
                    Id = "image-expand/flux-pro".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "reimagine-flux",
                    Type = "image",
                    Id = "reimagine-flux".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "image-upscaler",
                    Type = "image",
                    Id = "image-upscaler".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "image-upscaler-precision",
                    Type = "image",
                    Id = "image-upscaler-precision".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "image-upscaler-precision-v2",
                    Type = "image",
                    Id = "image-upscaler-precision-v2".ToModelId(GetIdentifier())
                },
                  new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "image-relight",
                    Type = "image",
                    Id = "image-relight".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "sound-effects",
                    Type = "speech",
                    Id = "sound-effects".ToModelId(GetIdentifier())
                }
            ]
            : []);
    }
}

