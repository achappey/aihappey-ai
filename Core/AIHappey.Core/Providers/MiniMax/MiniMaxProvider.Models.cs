using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.ModelProviders;

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
       // ===== MiniMax (Text) =====

       new()
    {
        Id = "minimax/MiniMax-M2.1",
        Name = "MiniMax M2.1",
        Description = "Open-source coding model optimized for agentic scenarios with strong multi-language programming, tool usage, instruction following, and long-range planning.",
        Type = "language",
        Created = 1766361600, // 2025-12-22 (release)
        OwnedBy = nameof(MiniMax)
    },

    new()
    {
        Id = "minimax/MiniMax-M2.1-lightning",
        Name = "MiniMax M2.1 Lightning",
        Description = "Faster version of MiniMax M2.1, offering the same performance with significantly higher throughput (~100 TPS).",
        Type = "language",
        Created = 1766361600, // 2025-12-22 (same release window as M2.1)
        OwnedBy = nameof(MiniMax)
    },

    new()
    {
        Id = "minimax/MiniMax-M2",
        Name = "MiniMax M2",
        Description = "Open-source model built for agents and code, designed for end-to-end development workflows.",
        Type = "language",
        Created = 1761523200, // 2025-10-27 (release)
        OwnedBy = nameof(MiniMax)
    },

    // ===== MiniMax Images =====
    // Expose MiniMax image model as "minimax/image-01" so it routes consistently through the resolver.

    new()
    {
        Id = "image-01".ToModelId(nameof(MiniMax).ToLowerInvariant()),
        Name = "MiniMax Image-01",
        Description = "Text-to-image generation model supporting multiple image sizes.",
        Type = "image",
        Created = 1739577600, // 2025-02-15 (Image-01 release)
        OwnedBy = nameof(MiniMax)
    },

    new()
    {
        Id = "image-01-live".ToModelId(nameof(MiniMax).ToLowerInvariant()),
        Name = "MiniMax Image-01 Live",
        Description = "Text-to-image generation model supporting multiple image sizes.",
        Type = "image",
        Created = 1739577600, // 2025-02-15 (no separate official release for “live”; alias)
        OwnedBy = nameof(MiniMax)
    },


    new()
    {
        Id = "speech-2.8-hd".ToModelId(nameof(MiniMax).ToLowerInvariant()),
        Name = "speech-2.8-hd",
        Description = "Latest HD speech model: perfecting tonal nuances and maximizing timbre similarity.",
        Type = "speech",
        OwnedBy = nameof(MiniMax)
    },

    new()
    {
        Id = "speech-2.8-turbo".ToModelId(nameof(MiniMax).ToLowerInvariant()),
        Name = "speech-2.8-turbo",
        Description = "Latest Turbo speech model: faster and more affordable, perfecting tonal nuances and maximizing timbre similarity.",
        Type = "speech",
        OwnedBy = nameof(MiniMax)
    },

    new()
    {
        Id = "speech-2.6-hd".ToModelId(nameof(MiniMax).ToLowerInvariant()),
        Name = "speech-2.6-hd",
        Description = "HD speech model with real-time response, intelligent parsing, and enhanced naturalness.",
        Type = "speech",
        Created = 1761696000, // 2025-10-29 (Speech-2.6 release)
        OwnedBy = nameof(MiniMax)
    },

    new()
    {
        Id = "speech-2.6-turbo".ToModelId(nameof(MiniMax).ToLowerInvariant()),
        Name = "speech-2.6-turbo",
        Description = "Turbo speech model: faster, more affordable, low-latency model designed for real-time applications and agents.",
        Type = "speech",
        Created = 1761696000, // 2025-10-29 (Speech-2.6 release)
        OwnedBy = nameof(MiniMax)
    },

    new()
    {
        Id = "speech-02-hd".ToModelId(nameof(MiniMax).ToLowerInvariant()),
        Name = "speech-02-hd",
        Description = "Speech-02 HD: high-fidelity text-to-audio with exceptional prosody, stability, voice similarity, and audio quality.",
        Type = "speech",
        Created = 1743552000, // 2025-04-02 (Speech-02 series release)
        OwnedBy = nameof(MiniMax)
    },

    new()
    {
        Id = "speech-02-turbo".ToModelId(nameof(MiniMax).ToLowerInvariant()),
        Name = "speech-02-turbo",
        Description = "Speech-02 Turbo: optimized for real-time performance with low latency while maintaining strong voice quality.",
        Type = "speech",
        Created = 1743552000, // 2025-04-02 (Speech-02 series release)
        OwnedBy = nameof(MiniMax)
    },

    new()
    {
        Id = "speech-01-hd".ToModelId(nameof(MiniMax).ToLowerInvariant()),
        Name = "speech-01-hd",
        Description = "Premium-quality voice synthesis with high-fidelity voice cloning from ~10 seconds of audio input, capturing voice characteristics and emotional nuances.",
        Type = "speech",
        Created = 1736899200, // 2025-01-15 (Speech-01-hd release)
        OwnedBy = nameof(MiniMax)
    },

    new()
    {
        Id = "speech-01-turbo".ToModelId(nameof(MiniMax).ToLowerInvariant()),
        Name = "speech-01-turbo",
        Description = "Speech synthesis model in the Speech-01 family (Turbo variant).",
        Type = "speech",
        OwnedBy = nameof(MiniMax)
    },

    // ===== MiniMax Speech (Music) =====

    new()
    {
        Id = "music-2.5".ToModelId(nameof(MiniMax).ToLowerInvariant()),
        Name = "music-2.5",
        Description = "Text-to-music model with human-like emotional vocals and enhanced multi-instrument performance; designed for professional studio quality and cohesive musical structure.",
        Type = "speech",
        Created = 1768521600, // 2026-01-16 (Music-2.5 release)
        OwnedBy = nameof(MiniMax)
    },

    new()
    {
        Id = "music-2.0".ToModelId(nameof(MiniMax).ToLowerInvariant()),
        Name = "music-2.0",
        Description = "Text-to-music model focused on enhanced musicality with natural vocals and smooth melodies, delivering richer emotional expression and stronger tone control.",
        Type = "speech",
        Created = 1761696000, // 2025-10-29 (Music-2.0 release)
        OwnedBy = nameof(MiniMax)
    },
];
}
