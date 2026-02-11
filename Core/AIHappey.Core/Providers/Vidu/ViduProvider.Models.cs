using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Vidu;

public partial class ViduProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        return await Task.FromResult(new List<Model>()
        {
            new()
            {
                Id = "viduq3-pro".ToModelId(GetIdentifier()),
                Name = "viduq3-pro",
                Type = "video"
            },
                new()
            {
                Id = "viduq2-pro-fast".ToModelId(GetIdentifier()),
                Name = "viduq2-pro-fast",
                Type = "video"
            },
                new()
            {
                Id = "viduq2-pro".ToModelId(GetIdentifier()),
                Name = "viduq2-pro",
                Type = "video"
            },
            new()
            {
                Id = "viduq2-turbo".ToModelId(GetIdentifier()),
                Name = "viduq2-turbo",
                Type = "video"
            },
            new()
            {
                Id = "viduq1".ToModelId(GetIdentifier()),
                Name = "viduq1",
                Type = "video"
            },
                new()
            {
                Id = "viduq1-classic".ToModelId(GetIdentifier()),
                Name = "viduq1-classic",
                Type = "video"
            },
                new()
            {
                Id = "vidu2.0".ToModelId(GetIdentifier()),
                Name = "vidu2.0",
                Type = "video"
            },
              new()
            {
                Id = "viduq2".ToModelId(GetIdentifier()),
                Name = "viduq2",
                Type = "video"
            },
              new()
            {
                Id = "viduq2".ToModelId(GetIdentifier()),
                Name = "viduq2",
                Type = "image"
            },
              new()
            {
                Id = "viduq1".ToModelId(GetIdentifier()),
                Name = "viduq1",
                Type = "image"
            },
              new()
            {
                Id = "audio1.0".ToModelId(GetIdentifier()),
                Name = "audio1.0",
                Type = "speech"
            },
              new()
            {
                Id = "audio-tts".ToModelId(GetIdentifier()),
                Name = "audio-tts",
                Type = "speech"
            }
        });
    }
}

