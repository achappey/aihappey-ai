using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Zai;

public partial class ZaiProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        return ZaiLanguageModels;
    }

    public static IReadOnlyList<Model> ZaiLanguageModels =>
    [
        new()
        {
            Id = "zai/glm-5",
            Name = "glm-5",
            ContextWindow = 200_000,
            MaxTokens = 128_000,
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4.7-flash",
            Name = "glm-4.7-flash",
            ContextWindow = 200_000,
            MaxTokens = 128_000,
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4.7-flashx",
            Name = "glm-4.7-flashx",
            ContextWindow = 200_000,
            MaxTokens = 128_000,
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4.7",
            Name = "glm-4.7",
            ContextWindow = 200_000,
            MaxTokens = 128_000,
            Description = "Latest flagship GLM-4.7 model, a foundational model specifically designed for agent applications.",
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4.6",
            ContextWindow = 200_000,
            MaxTokens = 128_000,
            Name = "glm-4.6",
            Description = "Previous-generation GLM flagship model with strong general reasoning and agent capabilities.",
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4.5",
            Name = "glm-4.5",
            ContextWindow = 128_000,
            MaxTokens = 96_000,
            Description = "GLM-4.5 base model offering balanced performance for general-purpose and agent workloads.",
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4.5-air",
            Name = "glm-4.5-air",
            Description = "Lightweight GLM-4.5 variant optimized for faster inference and lower latency.",
            ContextWindow = 128_000,
            MaxTokens = 96_000,
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4.5-x",
            Name = "glm-4.5-x",
            ContextWindow = 128_000,
            MaxTokens = 96_000,
            Description = "Enhanced GLM-4.5 variant with stronger reasoning and extended capability depth.",
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4.5-airx",
            Name = "glm-4.5-airx",
            ContextWindow = 128_000,
            MaxTokens = 96_000,
            Description = "Hybrid GLM-4.5 model combining efficiency of AIR with enhanced reasoning performance.",
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4.5-flash",
            Name = "glm-4.5-flash",
            ContextWindow = 128_000,
            MaxTokens = 96_000,
            Description = "Ultra-fast GLM-4.5 variant optimized for low-latency, high-throughput workloads.",
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4-32b-0414-128k",
            Name = "glm-4-32b-0414-128k",
            ContextWindow = 128_000,
            MaxTokens = 16_000,
            Description = "GLM-4 32B model with a 128k context window, suited for long-context reasoning and document-heavy tasks.",
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4.6v",
            Name = "glm-4.6v",
            ContextWindow = 128_000,
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4.6v-flashx",
            Name = "glm-4.6v-flashx",
            ContextWindow = 128_000,
            Type = "language",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-4.6v-flash",
            Name = "glm-4.6v-flash",
            Type = "language",
            ContextWindow = 128_000,
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/cogview-4-250304",
            Name = "cogview-4-250304",
            Type = "image",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-image",
            Name = "glm-image",
            Type = "image",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/glm-asr-2512",
            Name = "glm-asr-2512",
            Type = "transcription",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/cogvideox-3",
            Name = "cogvideox-3",
            Type = "video",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/viduq1-text",
            Name = "viduq1-text",
            Type = "video",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/viduq1-image",
            Name = "viduq1-image",
            Type = "video",
            OwnedBy = "z.ai"
        },
        new()
        {
            Id = "zai/vidu2-image",
            Name = "vidu2-image",
            Type = "video",
            OwnedBy = "z.ai"
        },
    ];

}
