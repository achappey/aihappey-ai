using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Model>>([.. DeepInfraLanguageModels, .. DeepInfraImageModels]);

    // Curated subset from the DeepInfra UI screenshots (text-generation only).
    public static IReadOnlyList<Model> DeepInfraLanguageModels =>
    [
        new()
        {
            Id = "nvidia/Nemotron-3-Nano-30B-A3B".ToModelId("deepinfra"),
            Name = "Nemotron-3-Nano-30B-A3B",
            Type = "language",
            OwnedBy = "NVIDIA",
            Description = "NVIDIA Nemotron 3 Nano (30B A3B)."
        },

        new() { Id = "deepseek-ai/DeepSeek-V3.2".ToModelId("deepinfra"), Name = "DeepSeek-V3.2", Type = "language", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/DeepSeek-V3.1".ToModelId("deepinfra"), Name = "DeepSeek-V3.1", Type = "language", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/DeepSeek-V3.1-Terminus".ToModelId("deepinfra"), Name = "DeepSeek-V3.1-Terminus", Type = "language", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/DeepSeek-V3-0324".ToModelId("deepinfra"), Name = "DeepSeek-V3-0324", Type = "language", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/DeepSeek-V3".ToModelId("deepinfra"), Name = "DeepSeek-V3", Type = "language", OwnedBy = "DeepSeek" },

        new() { Id = "deepseek-ai/DeepSeek-R1-0528".ToModelId("deepinfra"), Name = "DeepSeek-R1-0528", Type = "language", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/DeepSeek-R1".ToModelId("deepinfra"), Name = "DeepSeek-R1", Type = "language", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/DeepSeek-R1-0528-Turbo".ToModelId("deepinfra"), Name = "DeepSeek-R1-0528-Turbo", Type = "language", OwnedBy = "DeepSeek" },

        new() { Id = "moonshotai/Kimi-K2-Thinking".ToModelId("deepinfra"), Name = "Kimi-K2-Thinking", Type = "language", OwnedBy = "MoonshotAI" },
        new() { Id = "moonshotai/Kimi-K2-Instruct-0905".ToModelId("deepinfra"), Name = "Kimi-K2-Instruct-0905", Type = "language", OwnedBy = "MoonshotAI" },

        new() { Id = "openai/gpt-oss-120b".ToModelId("deepinfra"), Name = "gpt-oss-120b", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "openai/gpt-oss-20b".ToModelId("deepinfra"), Name = "gpt-oss-20b", Type = "language", OwnedBy = "OpenAI" },

        new() { Id = "Qwen/Qwen3-Next-80B-A3B-Instruct".ToModelId("deepinfra"), Name = "Qwen3-Next-80B-A3B-Instruct", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-Next-80B-A3B-Thinking".ToModelId("deepinfra"), Name = "Qwen3-Next-80B-A3B-Thinking", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-Coder-480B-A35B-Instruct".ToModelId("deepinfra"), Name = "Qwen3-Coder-480B-A35B-Instruct", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-Coder-480B-A35B-Instruct-Turbo".ToModelId("deepinfra"), Name = "Qwen3-Coder-480B-A35B-Instruct-Turbo", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-235B-A22B-Instruct-2507".ToModelId("deepinfra"), Name = "Qwen3-235B-A22B-Instruct-2507", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-235B-A22B-Thinking-2507".ToModelId("deepinfra"), Name = "Qwen3-235B-A22B-Thinking-2507", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-32B".ToModelId("deepinfra"), Name = "Qwen3-32B", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-30B-A3B".ToModelId("deepinfra"), Name = "Qwen3-30B-A3B", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-14B".ToModelId("deepinfra"), Name = "Qwen3-14B", Type = "language", OwnedBy = "Qwen" },

        new() { Id = "MiniMaxAI/MiniMax-M2".ToModelId("deepinfra"), Name = "MiniMax-M2", Type = "language", OwnedBy = "MiniMax" },
        new() { Id = "MiniMaxAI/MiniMax-M2.1".ToModelId("deepinfra"), Name = "MiniMax-M2.1", Type = "language", OwnedBy = "MiniMax" },
    ];

    // Curated subset from the DeepInfra UI screenshots (text-to-image only; excludes edit/erase/expand/background tools).
    public static IReadOnlyList<Model> DeepInfraImageModels =>
    [
        // ---- Bria ----
        new() { Id = "Bria/Bria-3.2".ToModelId("deepinfra"), Name = "Bria-3.2", Type = "image", OwnedBy = "Bria" },
        new() { Id = "Bria/Bria-3.2-vector".ToModelId("deepinfra"), Name = "Bria-3.2-vector", Type = "image", OwnedBy = "Bria" },
        new() { Id = "Bria/fibo".ToModelId("deepinfra"), Name = "fibo", Type = "image", OwnedBy = "Bria" },

        // ---- ByteDance ----
        new() { Id = "ByteDance/Seedream-4".ToModelId("deepinfra"), Name = "Seedream-4", Type = "image", OwnedBy = "ByteDance" },

        // ---- PrunaAI ----
        new() { Id = "PrunaAI/p-image".ToModelId("deepinfra"), Name = "p-image", Type = "image", OwnedBy = "PrunaAI" },

        // ---- Black Forest Labs ----
        new() { Id = "black-forest-labs/FLUX-1-dev".ToModelId("deepinfra"), Name = "FLUX-1-dev", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX-1-schnell".ToModelId("deepinfra"), Name = "FLUX-1-schnell", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX-1.1-pro".ToModelId("deepinfra"), Name = "FLUX-1.1-pro", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX-2-dev".ToModelId("deepinfra"), Name = "FLUX-2-dev", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX-2-pro".ToModelId("deepinfra"), Name = "FLUX-2-pro", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX-2-max".ToModelId("deepinfra"), Name = "FLUX-2-max", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX-pro".ToModelId("deepinfra"), Name = "FLUX-pro", Type = "image", OwnedBy = "BlackForestLabs" },

        // ---- DeepSeek ----
        new() { Id = "deepseek-ai/Janus-Pro-1B".ToModelId("deepinfra"), Name = "Janus-Pro-1B", Type = "image", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/Janus-Pro-7B".ToModelId("deepinfra"), Name = "Janus-Pro-7B", Type = "image", OwnedBy = "DeepSeek" },

        // ---- StabilityAI ----
        new() { Id = "stabilityai/sdxl-turbo".ToModelId("deepinfra"), Name = "sdxl-turbo", Type = "image", OwnedBy = "StabilityAI" },
    ];
}

