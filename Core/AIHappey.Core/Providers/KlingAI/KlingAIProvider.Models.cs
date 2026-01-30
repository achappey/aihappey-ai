using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.KlingAI;

public partial class KlingAIProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return BriaImageModels;
    }

    public static IReadOnlyList<Model> BriaImageModels =>
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
        }
    ];
}
