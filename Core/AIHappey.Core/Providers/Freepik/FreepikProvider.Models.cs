using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);


        // Hardcoded per user request.
        return await Task.FromResult<IEnumerable<Model>>(_keyResolver.Resolve(GetIdentifier()) != null
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
                    Name = "classic-fast",
                    Type = "image",
                    Id = "classic-fast".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "flux-2-pro",
                    Type = "image",
                    Id = "flux-2-pro".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "flux-2-turbo",
                    Type = "image",
                    Id = "flux-2-turbo".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "flux-dev",
                    Type = "image",
                    Id = "flux-dev".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "flux-pro-v1-1",
                    Type = "image",
                    Id = "flux-pro-v1-1".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "hyperflux",
                    Type = "image",
                    Id = "hyperflux".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedream",
                    Type = "image",
                    Id = "seedream".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedream-v4",
                    Type = "image",
                    Id = "seedream-v4".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedream-v4-edit",
                    Type = "image",
                    Id = "seedream-v4-edit".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedream-v4-5",
                    Type = "image",
                    Id = "seedream-v4-5".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "z-image",
                    Type = "image",
                    Id = "z-image".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "z-image-turbo",
                    Type = "image",
                    Id = "z-image-turbo".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedream-v4-5-edit",
                    Type = "image",
                    Id = "seedream-v4-5-edit".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "sound-effects",
                    Type = "speech",
                    Id = "sound-effects".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "runway-gen4-turbo",
                    Type = "video",
                    Id = "runway-gen4-turbo".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "ltx-2-pro",
                    Type = "video",
                    Id = "ltx-2-pro".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "kling-v2-6-pro",
                    Type = "video",
                    Id = "kling-v2-6-pro".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedance-1-5-pro-480p",
                    Type = "video",
                    Id = "seedance-1-5-pro-480p".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedance-1-5-pro-720p",
                    Type = "video",
                    Id = "seedance-1-5-pro-720p".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedance-1-5-pro-1080p",
                    Type = "video",
                    Id = "seedance-1-5-pro-1080p".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedance-pro-480p",
                    Type = "video",
                    Id = "seedance-pro-480p".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedance-pro-720p",
                    Type = "video",
                    Id = "seedance-pro-720p".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedance-pro-1080p",
                    Type = "video",
                    Id = "seedance-pro-1080p".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedance-lite-480p",
                    Type = "video",
                    Id = "seedance-lite-480p".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedance-lite-720p",
                    Type = "video",
                    Id = "seedance-lite-720p".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "seedance-lite-1080p",
                    Type = "video",
                    Id = "seedance-lite-1080p".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "Mystic Zen",
                    Type = "image",
                    Id = "mystic/zen".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "Mystic Flexible",
                    Type = "image",
                    Id = "mystic/flexible".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "Mystic Fluid",
                    Type = "image",
                    Id = "mystic/fluid".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "Mystic Realism",
                    Type = "image",
                    Id = "mystic/realism".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "Mystic Super Real",
                    Type = "image",
                    Id = "mystic/super_real".ToModelId(GetIdentifier())
                },
                new Model
                {
                    OwnedBy = nameof(Freepik),
                    Name = "Mystic Editorial Portraits",
                    Type = "image",
                    Id = "mystic/editorial_portraits".ToModelId(GetIdentifier())
                }
            ]
            : []);
    }
}

