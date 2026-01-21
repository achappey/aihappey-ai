using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Hyperbolic;

public partial class HyperbolicProvider : IModelProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return [.. HyperbolicModels, .. HyperbolicImageModels, .. HyperbolicSpeechModels];
    }

    public IReadOnlyList<Model> HyperbolicSpeechModels =>
    [
        new()
        {
            Id = "audio-generation".ToModelId(GetIdentifier()),
            Name = "audio-generation",
            Description = "Hyperbolic audio generation.",
            Type = "speech",
            OwnedBy = nameof(Hyperbolic)
        }
    ];

    public IReadOnlyList<Model> HyperbolicModels =>
    [
        // ===== Qwen =====
        new()
        {
            Id = "Qwen/Qwen3-Next-80B-A3B-Thinking".ToModelId(GetIdentifier()),
            Name = "Qwen3-Next-80B-A3B-Thinking",
            Description = "Qwen3-Next thinker model.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen3-Next-80B-A3B-Instruct".ToModelId(GetIdentifier()),
            Name = "Qwen3-Next-80B-A3B-Instruct",
            Description = "Qwen3-Next instruct model.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen3-Coder-480B-A35B-Instruct".ToModelId(GetIdentifier()),
            Name = "Qwen3-Coder-480B-A35B-Instruct",
            Description = "The latest and most powerful coder model from the Qwen Team.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen3-235B-A22B-Instruct-2507".ToModelId(GetIdentifier()),
            Name = "Qwen3-235B-A22B-Instruct-2507",
            Description = "Qwen latest non-thinking model with significant improvements in general capabilities.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen3-235B-A22B".ToModelId(GetIdentifier()),
            Name = "Qwen3-235B-A22B",
            Description = "A mixture-of-experts (MoE) model by Qwen with strong reasoning and agent tool-calling capabilities.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/QwQ-32B".ToModelId(GetIdentifier()),
            Name = "QwQ-32B",
            Description = "The latest Qwen reasoning model.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen2.5-Coder-32B".ToModelId(GetIdentifier()),
            Name = "Qwen2.5-Coder-32B",
            Description = "The best coder model from the Qwen Team.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen2.5-72B".ToModelId(GetIdentifier()),
            Name = "Qwen2.5-72B",
            Description = "The latest Qwen LLM with more knowledge in coding and math.",
            Type = "language",
            OwnedBy = "Qwen"
        },

        // ===== OpenAI (OSS) =====
        new()
        {
            Id = "openai/gpt-oss-120b".ToModelId(GetIdentifier()),
            Name = "gpt-oss-120b",
            Description = "OpenAI’s open-weight model (big model smell).",
            Type = "language",
            OwnedBy = "OpenAI"
        },
        new()
        {
            Id = "openai/gpt-oss-20b".ToModelId(GetIdentifier()),
            Name = "gpt-oss-20b",
            Description = "OpenAI’s open-weight model (small model smell).",
            Type = "language",
            OwnedBy = "OpenAI"
        },

        // ===== Moonshot =====
        new()
        {
            Id = "moonshotai/Kimi-K2".ToModelId(GetIdentifier()),
            Name = "Kimi-K2",
            Description = "Kimi's latest 1T LLM, good at coding and tool-calling.",
            Type = "language",
            OwnedBy = "Moonshot"
        },

        // ===== DeepSeek =====
        new()
        {
            Id = "deepseek-ai/DeepSeek-R1-0528".ToModelId(GetIdentifier()),
            Name = "DeepSeek-R1-0528",
            Description = "The latest open-source reasoner LLM released by DeepSeek.",
            Type = "language",
            OwnedBy = "DeepSeek"
        },
        new()
        {
            Id = "deepseek-ai/DeepSeek-R1".ToModelId(GetIdentifier()),
            Name = "DeepSeek-R1",
            Description = "The best open-source reasoner LLM released by DeepSeek.",
            Type = "language",
            OwnedBy = "DeepSeek"
        },
        new()
        {
            Id = "deepseek-ai/DeepSeek-V3-0324".ToModelId(GetIdentifier()),
            Name = "DeepSeek-V3-0324",
            Description = "DeepSeek's updated V3 model released on 03/24/2025.",
            Type = "language",
            OwnedBy = "DeepSeek"
        },
        new()
        {
            Id = "deepseek-ai/DeepSeek-V3".ToModelId(GetIdentifier()),
            Name = "DeepSeek-V3",
            Description = "The best open-source LLM released by DeepSeek.",
            Type = "language",
            OwnedBy = "DeepSeek"
        },

        // ===== Meta =====
        new()
        {
            Id = "meta-llama/Llama-3.3-70B".ToModelId(GetIdentifier()),
            Name = "Llama-3.3-70B",
            Description = "Meta's latest 70B LLM with performance comparable to Llama 3.1 405B.",
            Type = "language",
            OwnedBy = "Meta"
        },
        new()
        {
            Id = "meta-llama/Llama-3.2-3B".ToModelId(GetIdentifier()),
            Name = "Llama-3.2-3B",
            Description = "The latest Llama 3.2 instruction-tuned model by Meta.",
            Type = "language",
            OwnedBy = "Meta"
        },
        new()
        {
            Id = "meta-llama/Llama-3-70B".ToModelId(GetIdentifier()),
            Name = "Llama-3-70B",
            Description = "A highly efficient and powerful model designed for a variety of tasks.",
            Type = "language",
            OwnedBy = "Meta"
        },
        new()
        {
            Id = "meta-llama/Llama-3.1-405B".ToModelId(GetIdentifier()),
            Name = "Llama-3.1-405B",
            Description = "The biggest and best open-source AI model trained by Meta, beating GPT-4o across most benchmarks.",
            Type = "language",
            OwnedBy = "Meta"
        },
        new()
        {
            Id = "meta-llama/Llama-3.1-70B".ToModelId(GetIdentifier()),
            Name = "Llama-3.1-70B",
            Description = "The best LLM at its size with faster response times compared to the 405B model.",
            Type = "language",
            OwnedBy = "Meta"
        },
        new()
        {
            Id = "meta-llama/Llama-3.1-8B".ToModelId(GetIdentifier()),
            Name = "Llama-3.1-8B",
            Description = "The smallest and fastest member of the Llama 3.1 family.",
            Type = "language",
            OwnedBy = "Meta"
        },
    ];

    public IReadOnlyList<Model> HyperbolicImageModels =>
[
    new()
    {
        Id = "FLUX.1-dev".ToModelId(GetIdentifier()),
        Name = "FLUX.1-dev",
        Description = "State-of-the-art image generation model with excellent prompt following and visual fidelity.",
        Type = "image",
        OwnedBy = "BlackForestLabs"
    },

    new()
    {
        Id = "SDXL1.0-base".ToModelId(GetIdentifier()),
        Name = "SDXL-1.0",
        Description = "High-resolution image generation with strong detail and composition.",
        Type = "image",
        OwnedBy = "StabilityAI"
    },

    new()
    {
        Id = "SD1.5".ToModelId(GetIdentifier()),
        Name = "Stable Diffusion 1.5",
        Description = "Reliable and versatile general-purpose image generation model.",
        Type = "image",
        OwnedBy = "StabilityAI"
    },

    new()
    {
        Id = "SD2".ToModelId(GetIdentifier()),
        Name = "Stable Diffusion 2",
        Description = "Improved generation quality over SD 1.5.",
        Type = "image",
        OwnedBy = "StabilityAI"
    },

    new()
    {
        Id = "SSD".ToModelId(GetIdentifier()),
        Name = "Stable Speed Diffusion",
        Description = "Optimized Stable Diffusion variant focused on faster inference.",
        Type = "image",
        OwnedBy = "StabilityAI"
    },

    new()
    {
        Id = "SDXL-turbo".ToModelId(GetIdentifier()),
        Name = "SDXL Turbo",
        Description = "Fast SDXL variant with reduced latency.",
        Type = "image",
        OwnedBy = "StabilityAI"
    },

    new()
    {
        Id = "SD1.5-ControlNet".ToModelId(GetIdentifier()),
        Name = "SD 1.5 ControlNet",
        Description = "Stable Diffusion 1.5 with ControlNet conditioning support.",
        Type = "image",
        OwnedBy = "StabilityAI"
    },

    new()
    {
        Id = "SDXL-ControlNet".ToModelId(GetIdentifier()),
        Name = "SDXL ControlNet",
        Description = "SDXL with ControlNet for structure-guided generation.",
        Type = "image",
        OwnedBy = "StabilityAI"
    },

    new()
    {
        Id = "playground-v2.5".ToModelId(GetIdentifier()),
        Name = "Playground v2.5",
        Description = "Creative image generation model tuned for aesthetics.",
        Type = "image",
        OwnedBy = "PlaygroundAI"
    },

    new()
    {
        Id = "Fluently-XL-v4".ToModelId(GetIdentifier()),
        Name = "Fluently XL v4",
        Description = "High-quality XL model optimized for fluent visual output.",
        Type = "image",
        OwnedBy = "FluentlyAI"
    },

    new()
    {
        Id = "Fluently-XL-Final".ToModelId(GetIdentifier()),
        Name = "Fluently XL Final",
        Description = "Final Fluently XL release with refined visual consistency.",
        Type = "image",
        OwnedBy = "FluentlyAI"
    },

    new()
    {
        Id = "PixArt-Sigma-XL-2-1024-MS".ToModelId(GetIdentifier()),
        Name = "PixArt Sigma XL 2",
        Description = "Diffusion transformer model focused on photorealism and scale.",
        Type = "image",
        OwnedBy = "PixArt"
    },

    new()
    {
        Id = "dreamshaper-xl-lightning".ToModelId(GetIdentifier()),
        Name = "DreamShaper XL Lightning",
        Description = "Stylized SDXL model with lightning-fast generation.",
        Type = "image",
        OwnedBy = "DreamShaper"
    },
];

}
