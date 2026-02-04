using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        // Alibaba DashScope does not expose a public list-models endpoint for compatible-mode.
        // We hardcode common Qwen "flagship" model names.
        ApplyAuthHeader();

        return Task.FromResult<IEnumerable<Model>>(
        [
            new()
            {
                Id = "qwen-max".ToModelId(GetIdentifier()),
                Name = "qwen-max",
                Type = "language",
                OwnedBy = nameof(Alibaba),
                ContextWindow = 262144,
            },
            new()
            {
                Id = "qwen-plus".ToModelId(GetIdentifier()),
                Name = "qwen-plus",
                Type = "language",
                OwnedBy = nameof(Alibaba),
                ContextWindow = 1000000,
            },
            new()
            {
                Id = "qwen-flash".ToModelId(GetIdentifier()),
                Name = "qwen-flash",
                Type = "language",
                OwnedBy = nameof(Alibaba),
                ContextWindow = 1000000,
            },
            new()
            {
                Id = "qwen-coder".ToModelId(GetIdentifier()),
                Name = "qwen-coder",
                Type = "language",
                OwnedBy = nameof(Alibaba),
                ContextWindow = 1000000,
            },

            // ---- Image generation (Qwen-Image) ----
            new()
            {
                Id = "qwen-image-max".ToModelId(GetIdentifier()),
                Name = "qwen-image-max",
                Type = "image",
                OwnedBy = nameof(Alibaba),
            },
            new()
            {
                Id = "qwen-image-max-2025-12-30".ToModelId(GetIdentifier()),
                Name = "qwen-image-max-2025-12-30",
                Type = "image",
                OwnedBy = nameof(Alibaba),
            },
            new()
            {
                Id = "qwen-image-plus".ToModelId(GetIdentifier()),
                Name = "qwen-image-plus",
                Type = "image",
                OwnedBy = nameof(Alibaba),
            },
            new()
            {
                Id = "qwen-image".ToModelId(GetIdentifier()),
                Name = "qwen-image",
                Type = "image",
                OwnedBy = nameof(Alibaba),
            },

            // ---- Image generation (Tongyi Z-Image) ----
            new()
            {
                Id = "z-image-turbo".ToModelId(GetIdentifier()),
                Name = "z-image-turbo",
                Type = "image",
                OwnedBy = nameof(Alibaba),
            },

            // ---- Image generation (Wan 2.6) ----
            new()
            {
                Id = "wan2.6-image".ToModelId(GetIdentifier()),
                Name = "wan2.6-image",
                Type = "image",
                OwnedBy = nameof(Alibaba),
            },
            new()
            {
                Id = "wan2.6-t2i".ToModelId(GetIdentifier()),
                Name = "wan2.6-t2i",
                Type = "image",
                OwnedBy = nameof(Alibaba),
            },

            // ---- Video generation (Wan) ----
            new()
            {
                Id = "wan2.6-i2v-flash".ToModelId(GetIdentifier()),
                Name = "wan2.6-i2v-flash",
                Type = "video",
                OwnedBy = nameof(Alibaba),
            },
            new()
            {
                Id = "wan2.6-i2v".ToModelId(GetIdentifier()),
                Name = "wan2.6-i2v",
                Type = "video",
                OwnedBy = nameof(Alibaba),
            },
            new()
            {
                Id = "wan2.5-i2v-preview".ToModelId(GetIdentifier()),
                Name = "wan2.5-i2v-preview",
                Type = "video",
                OwnedBy = nameof(Alibaba),
            },
            new()
            {
                Id = "wan2.6-t2v".ToModelId(GetIdentifier()),
                Name = "wan2.6-t2v",
                Type = "video",
                OwnedBy = nameof(Alibaba),
            },
            new()
            {
                Id = "wan2.5-t2v-preview".ToModelId(GetIdentifier()),
                Name = "wan2.5-t2v-preview",
                Type = "video",
                OwnedBy = nameof(Alibaba),
            }
        ]);
    }
}

