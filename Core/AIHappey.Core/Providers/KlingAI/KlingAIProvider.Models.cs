using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.KlingAI;

public partial class KlingAIProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
                if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);


        ApplyAuthHeader();

        return KlingModels;
    }

    public static IReadOnlyList<Model> KlingModels =>
    [
        new()
        {
            Id = "klingai/kling-v1",
            Name = "kling-v1",
            Type = "image",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-v1-5",
            Name = "kling-v1-5",
            Type = "image",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-v2",
            Name = "kling-v2",
            Type = "image",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-v2-new",
            Name = "kling-v2-new",
            Type = "image",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-v2-1",
            Name = "kling-v2-1",
            Type = "image",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-v1",
            Name = "kling-v1",
            Type = "video",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-v1-5",
            Name = "kling-v1-5",
            Type = "video",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-v1-6",
            Name = "kling-v1-6",
            Type = "video",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-v2-master",
            Name = "kling-v2-master",
            Type = "video",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-v2-1",
            Name = "kling-v2-1",
            Type = "video",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-v2-1-master",
            Name = "kling-v2-1-master",
            Type = "video",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-v2-5-turbo",
            Name = "kling-v2-5-turbo",
            Type = "video",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-v2-6",
            Name = "kling-v2-6",
            Type = "video",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/kling-video-o1",
            Name = "kling-video-o1",
            Type = "video",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/avatar",
            Name = "avatar",
            Type = "video",
            OwnedBy = "Kling AI"
        },
        new()
        {
            Id = "klingai/text-to-audio",
            Name = "text-to-audio",
            Type = "speech",
            OwnedBy = "Kling AI"
        }
    ];
}
