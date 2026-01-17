using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.MiniMax;

public partial class MiniMaxProvider : IModelProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return MiniMaxModels;
    }

    public static IReadOnlyList<Model> MiniMaxModels =>
        [
            // ===== MiniMax =====
            new() { Id = "minimax/MiniMax-M2.1",
                Name = "MiniMax M2.1",
                Type = "language",
                OwnedBy = nameof(MiniMax) },
            new() { Id = "minimax/MiniMax-M2.1-lightning",
                Name = "MiniMax M2.1 Lightning",
                Type = "language",
                OwnedBy = nameof(MiniMax) },
            new() { Id = "minimax/MiniMax-M2",
                Name = "MiniMax M2",
                Type = "language",
                OwnedBy = nameof(MiniMax) },

            // ===== MiniMax Images =====
            // Expose MiniMax image model as "minimax/image-01" so it routes consistently through the resolver.
            new()
            {
                Id = "image-01".ToModelId(nameof(MiniMax).ToLowerInvariant()),
                Name = "MiniMax Image",
                Type = "image",
                OwnedBy = nameof(MiniMax)
            },

            // ===== MiniMax Speech (Text-to-Audio) =====
            new() { Id = "speech-2.6-hd".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "speech-2.6-hd", Type = "speech", OwnedBy = nameof(MiniMax) },
            new() { Id = "speech-2.6-turbo".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "speech-2.6-turbo", Type = "speech", OwnedBy = nameof(MiniMax) },
            new() { Id = "speech-02-hd".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "speech-02-hd", Type = "speech", OwnedBy = nameof(MiniMax) },
            new() { Id = "speech-02-turbo".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "speech-02-turbo", Type = "speech", OwnedBy = nameof(MiniMax) },
            new() { Id = "speech-01-hd".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "speech-01-hd", Type = "speech", OwnedBy = nameof(MiniMax) },
            new() { Id = "speech-01-turbo".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "speech-01-turbo", Type = "speech", OwnedBy = nameof(MiniMax) },

            // ===== MiniMax Speech (Music) =====
            new() { Id = "music-2.0".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "music-2.0", Type = "speech", OwnedBy = nameof(MiniMax) },
        ];

}
