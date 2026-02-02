using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Runway;

public partial class RunwayProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default) =>
        await Task.FromResult<IEnumerable<Model>>(
            _keyResolver.Resolve(GetIdentifier()) != null
                ?
                [
                    new Model()
                    {
                        OwnedBy = nameof(Runway),
                        Name = "gen4_image",
                        Type = "image",
                        Id = "gen4_image".ToModelId(GetIdentifier())
                    },
                    new Model()
                    {
                        OwnedBy = nameof(Runway),
                        Type = "image",
                        Name = "gen4_image_turbo",
                        Id = "gen4_image_turbo".ToModelId(GetIdentifier())
                    },
                    new Model()
                    {
                        OwnedBy = nameof(Runway),
                        Type = "image",
                        Name = "gemini_2.5_flash",
                        Id = "gemini_2.5_flash".ToModelId(GetIdentifier())
                    },
                    // Runway Text-to-Speech
                    new Model()
                    {
                        OwnedBy = nameof(Runway),
                        Type = "speech",
                        Name = "eleven_multilingual_v2",
                        Id = "eleven_multilingual_v2".ToModelId(GetIdentifier())
                    },
                    // Runway Sound Effects (Text-to-Sound)
                    new Model()
                    {
                        OwnedBy = nameof(Runway),
                        Type = "speech",
                        Name = "eleven_text_to_sound_v2",
                        Id = "eleven_text_to_sound_v2".ToModelId(GetIdentifier())
                    },
                    new Model()
                    {
                        OwnedBy = nameof(Runway),
                        Type = "video",
                        Name = "veo3.1",
                        Id = "veo3.1".ToModelId(GetIdentifier())
                    },
                    new Model()
                    {
                        OwnedBy = nameof(Runway),
                        Type = "video",
                        Name = "veo3.1_fast",
                        Id = "veo3.1_fast".ToModelId(GetIdentifier())
                    },
                    new Model()
                    {
                        OwnedBy = nameof(Runway),
                        Type = "video",
                        Name = "veo3",
                        Id = "veo3".ToModelId(GetIdentifier())
                    },
                      new Model()
                    {
                        OwnedBy = nameof(Runway),
                        Type = "video",
                        Name = "gen4_turbo",
                        Id = "gen4_turbo".ToModelId(GetIdentifier())
                    },
                      new Model()
                    {
                        OwnedBy = nameof(Runway),
                        Type = "video",
                        Name = "gen3a_turbo",
                        Id = "gen3a_turbo".ToModelId(GetIdentifier())
                    }
                ]
                : []);
}
