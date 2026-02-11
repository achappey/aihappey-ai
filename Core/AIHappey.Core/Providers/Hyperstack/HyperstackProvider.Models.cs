using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Hyperstack;

public partial class HyperstackProvider : IModelProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);


        ApplyAuthHeader();

        return [.. HyperstackModels];
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IReadOnlyList<Model> HyperstackModels =>
[
    new() { Id = "nvidia/Llama-3_1-Nemotron-Ultra-253B-v1".ToModelId(GetIdentifier()), Name = "Llama-3_1-Nemotron-Ultra-253B-v1", Type = "language", OwnedBy = "NVIDIA" },
    new() { Id = "Qwen/Qwen3-8B".ToModelId(GetIdentifier()), Name = "Qwen3-8B", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "MiniMaxAI/MiniMax-M2".ToModelId(GetIdentifier()), Name = "MiniMax-M2", Type = "language", OwnedBy = "MiniMax" },
    new() { Id = "google/gemma-2-2b-it".ToModelId(GetIdentifier()), Name = "gemma-2-2b-it", Type = "language", OwnedBy = "Google" },
    new() { Id = "Qwen/Qwen3-4B-Thinking-2507".ToModelId(GetIdentifier()), Name = "Qwen3-4B-Thinking-2507", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "deepseek-ai/DeepSeek-V3-0324".ToModelId(GetIdentifier()), Name = "DeepSeek-V3-0324", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "alpindale/WizardLM-2-8x22B".ToModelId(GetIdentifier()), Name = "WizardLM-2-8x22B", Type = "language", OwnedBy = "Alpindale" },
    new() { Id = "Qwen/QwQ-32B".ToModelId(GetIdentifier()), Name = "QwQ-32B", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "zai-org/GLM-4.6".ToModelId(GetIdentifier()), Name = "GLM-4.6", Type = "language", OwnedBy = "ZAI" },
    new() { Id = "deepcogito/cogito-671b-v2.1".ToModelId(GetIdentifier()), Name = "cogito-671b-v2.1", Type = "language", OwnedBy = "DeepCogito" },

    // Hyperstack-native
    new() { Id = "meta-llama/Llama-3.3-70B-Instruct".ToModelId(GetIdentifier()), Name = "Llama 3.3 70B Instruct", Type = "language", OwnedBy = "Meta" },
    new() { Id = "mistralai/Mistral-Small-24B-Instruct-2501".ToModelId(GetIdentifier()), Name = "Mistral Small 24B Instruct 2501", Type = "language", OwnedBy = "Mistral" },
    new() { Id = "openai/gpt-oss-120b".ToModelId(GetIdentifier()), Name = "gpt-oss-120b", Type = "language", OwnedBy = "OpenAI" },
    new() { Id = "meta-llama/Llama-3.1-8B-Instruct".ToModelId(GetIdentifier()), Name = "Llama-3.1-8B-Instruct", Type = "language", OwnedBy = "Meta" },

    new() { Id = "Qwen/Qwen3-4B-Instruct-2507".ToModelId(GetIdentifier()), Name = "Qwen3-4B-Instruct-2507", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-32B".ToModelId(GetIdentifier()), Name = "Qwen3-32B", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B-FP8".ToModelId(GetIdentifier()), Name = "NVIDIA-Nemotron-3-Nano-30B-A3B-FP8", Type = "language", OwnedBy = "NVIDIA" },
    new() { Id = "EssentialAI/rnj-1-instruct".ToModelId(GetIdentifier()), Name = "rnj-1-instruct", Type = "language", OwnedBy = "EssentialAI" },
    new() { Id = "deepseek-ai/DeepSeek-Prover-V2-671B".ToModelId(GetIdentifier()), Name = "DeepSeek-Prover-V2-671B", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "moonshotai/Kimi-K2-Instruct-0905".ToModelId(GetIdentifier()), Name = "Kimi-K2-Instruct-0905", Type = "language", OwnedBy = "Moonshot" },
    new() { Id = "NousResearch/Hermes-2-Pro-Llama-3-8B".ToModelId(GetIdentifier()), Name = "Hermes-2-Pro-Llama-3-8B", Type = "language", OwnedBy = "NousResearch" },
    new() { Id = "MiniMaxAI/MiniMax-M2.1".ToModelId(GetIdentifier()), Name = "MiniMax-M2.1", Type = "language", OwnedBy = "MiniMax" },
    new() { Id = "zai-org/GLM-4.5-Air".ToModelId(GetIdentifier()), Name = "GLM-4.5-Air", Type = "language", OwnedBy = "ZAI" },
    new() { Id = "meta-llama/Meta-Llama-3-8B-Instruct".ToModelId(GetIdentifier()), Name = "Meta-Llama-3-8B-Instruct", Type = "language", OwnedBy = "Meta" },
    new() { Id = "Sao10K/L3-70B-Euryale-v2.1".ToModelId(GetIdentifier()), Name = "L3-70B-Euryale-v2.1", Type = "language", OwnedBy = "Sao10K" },
    new() { Id = "Qwen/Qwen2.5-Coder-7B-Instruct".ToModelId(GetIdentifier()), Name = "Qwen2.5-Coder-7B-Instruct", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "NousResearch/Hermes-4-405B".ToModelId(GetIdentifier()), Name = "Hermes-4-405B", Type = "language", OwnedBy = "NousResearch" },
    new() { Id = "moonshotai/Kimi-K2-Thinking".ToModelId(GetIdentifier()), Name = "Kimi-K2-Thinking", Type = "language", OwnedBy = "Moonshot" },
    new() { Id = "marin-community/marin-8b-instruct".ToModelId(GetIdentifier()), Name = "marin-8b-instruct", Type = "language", OwnedBy = "Marin" },
    new() { Id = "deepseek-ai/DeepSeek-R1".ToModelId(GetIdentifier()), Name = "DeepSeek-R1", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "google/gemma-2-9b-it".ToModelId(GetIdentifier()), Name = "gemma-2-9b-it", Type = "language", OwnedBy = "Google" },
    new() { Id = "PrimeIntellect/INTELLECT-3-FP8".ToModelId(GetIdentifier()), Name = "INTELLECT-3-FP8", Type = "language", OwnedBy = "PrimeIntellect" },

    new() { Id = "Qwen/Qwen3-Next-80B-A3B-Thinking".ToModelId(GetIdentifier()), Name = "Qwen3-Next-80B-A3B-Thinking", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-Next-80B-A3B-Instruct".ToModelId(GetIdentifier()), Name = "Qwen3-Next-80B-A3B-Instruct", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-Coder-30B-A3B-Instruct".ToModelId(GetIdentifier()), Name = "Qwen3-Coder-30B-A3B-Instruct", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-Coder-480B-A35B-Instruct".ToModelId(GetIdentifier()), Name = "Qwen3-Coder-480B-A35B-Instruct", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-Coder-480B-A35B-Instruct-FP8".ToModelId(GetIdentifier()), Name = "Qwen3-Coder-480B-A35B-Instruct-FP8", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-235B-A22B".ToModelId(GetIdentifier()), Name = "Qwen3-235B-A22B", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-235B-A22B-FP8".ToModelId(GetIdentifier()), Name = "Qwen3-235B-A22B-FP8", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-235B-A22B-Instruct-2507".ToModelId(GetIdentifier()), Name = "Qwen3-235B-A22B-Instruct-2507", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-235B-A22B-Thinking-2507".ToModelId(GetIdentifier()), Name = "Qwen3-235B-A22B-Thinking-2507", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-14B".ToModelId(GetIdentifier()), Name = "Qwen3-14B", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-30B-A3B".ToModelId(GetIdentifier()), Name = "Qwen3-30B-A3B", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-30B-A3B-Instruct-2507".ToModelId(GetIdentifier()), Name = "Qwen3-30B-A3B-Instruct-2507", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen3-30B-A3B-Thinking-2507".ToModelId(GetIdentifier()), Name = "Qwen3-30B-A3B-Thinking-2507", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen2.5-7B-Instruct".ToModelId(GetIdentifier()), Name = "Qwen2.5-7B-Instruct", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen2.5-72B-Instruct".ToModelId(GetIdentifier()), Name = "Qwen2.5-72B-Instruct", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen2.5-Coder-3B-Instruct".ToModelId(GetIdentifier()), Name = "Qwen2.5-Coder-3B-Instruct", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen2.5-Coder-32B-Instruct".ToModelId(GetIdentifier()), Name = "Qwen2.5-Coder-32B-Instruct", Type = "language", OwnedBy = "Qwen" },
    new() { Id = "Qwen/Qwen2.5-Coder-7B".ToModelId(GetIdentifier()), Name = "Qwen2.5-Coder-7B", Type = "language", OwnedBy = "Qwen" },

    // ===== DEEPSEEK (missing) =====
    new() { Id = "deepseek-ai/DeepSeek-R1-0528".ToModelId(GetIdentifier()), Name = "DeepSeek-R1-0528", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "deepseek-ai/DeepSeek-R1-0528-Qwen3-8B".ToModelId(GetIdentifier()), Name = "DeepSeek-R1-0528-Qwen3-8B", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "deepseek-ai/DeepSeek-R1-Distill-Qwen-1.5B".ToModelId(GetIdentifier()), Name = "DeepSeek-R1-Distill-Qwen-1.5B", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "deepseek-ai/DeepSeek-R1-Distill-Qwen-7B".ToModelId(GetIdentifier()), Name = "DeepSeek-R1-Distill-Qwen-7B", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "deepseek-ai/DeepSeek-R1-Distill-Qwen-14B".ToModelId(GetIdentifier()), Name = "DeepSeek-R1-Distill-Qwen-14B", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "deepseek-ai/DeepSeek-R1-Distill-Qwen-32B".ToModelId(GetIdentifier()), Name = "DeepSeek-R1-Distill-Qwen-32B", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "deepseek-ai/DeepSeek-R1-Distill-Llama-8B".ToModelId(GetIdentifier()), Name = "DeepSeek-R1-Distill-Llama-8B", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "deepseek-ai/DeepSeek-V3".ToModelId(GetIdentifier()), Name = "DeepSeek-V3", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "deepseek-ai/DeepSeek-V3.1".ToModelId(GetIdentifier()), Name = "DeepSeek-V3.1", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "deepseek-ai/DeepSeek-V3.1-Terminus".ToModelId(GetIdentifier()), Name = "DeepSeek-V3.1-Terminus", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "deepseek-ai/DeepSeek-V3.2".ToModelId(GetIdentifier()), Name = "DeepSeek-V3.2", Type = "language", OwnedBy = "DeepSeek" },
    new() { Id = "deepseek-ai/DeepSeek-V3.2-Exp".ToModelId(GetIdentifier()), Name = "DeepSeek-V3.2-Exp", Type = "language", OwnedBy = "DeepSeek" },

    // ===== NVIDIA (missing) =====
    new() { Id = "nvidia/NVIDIA-Nemotron-Nano-12B-v2".ToModelId(GetIdentifier()), Name = "NVIDIA-Nemotron-Nano-12B-v2", Type = "language", OwnedBy = "NVIDIA" },

    // ===== META / LLAMA (missing) =====
    new() { Id = "meta-llama/Llama-3.2-3B-Instruct".ToModelId(GetIdentifier()), Name = "Llama-3.2-3B-Instruct", Type = "language", OwnedBy = "Meta" },
    new() { Id = "tokyotech-llm/Llama-3.3-Swallow-70B-Instruct-v0.4".ToModelId(GetIdentifier()), Name = "Llama-3.3-Swallow-70B-Instruct-v0.4", Type = "language", OwnedBy = "TokyoTech" },

    // ===== BAIDU =====
    new() { Id = "baidu/ERNIE-4.5-21B-A3B-PT".ToModelId(GetIdentifier()), Name = "ERNIE-4.5-21B-A3B-PT", Type = "language", OwnedBy = "Baidu" },
    new() { Id = "baidu/ERNIE-4.5-300B-A47B-Base-PT".ToModelId(GetIdentifier()), Name = "ERNIE-4.5-300B-A47B-Base-PT", Type = "language", OwnedBy = "Baidu" },

    // ===== ZAI / GLM =====
    new() { Id = "zai-org/GLM-4.5".ToModelId(GetIdentifier()), Name = "GLM-4.5", Type = "language", OwnedBy = "ZAI" },
    new() { Id = "zai-org/GLM-4.7".ToModelId(GetIdentifier()), Name = "GLM-4.7", Type = "language", OwnedBy = "ZAI" },
    new() { Id = "zai-org/GLM-4.5-Air-FP8".ToModelId(GetIdentifier()), Name = "GLM-4.5-Air-FP8", Type = "language", OwnedBy = "ZAI" },

    // ===== MINIMAX =====
    new() { Id = "MiniMaxAI/MiniMax-M1-80k".ToModelId(GetIdentifier()), Name = "MiniMax-M1-80k", Type = "language", OwnedBy = "MiniMax" },

    // ===== DEEPCOGITO =====
    new() { Id = "deepcogito/cogito-671b-v2.1-FP8".ToModelId(GetIdentifier()), Name = "cogito-671b-v2.1-FP8", Type = "language", OwnedBy = "DeepCogito" },
    new() { Id = "deepcogito/cogito-v2-preview-llama-70B".ToModelId(GetIdentifier()), Name = "cogito-v2-preview-llama-70B", Type = "language", OwnedBy = "DeepCogito" },
    new() { Id = "deepcogito/cogito-v2-preview-llama-405B".ToModelId(GetIdentifier()), Name = "cogito-v2-preview-llama-405B", Type = "language", OwnedBy = "DeepCogito" },

    // ===== OPENAI =====
    new() { Id = "openai/gpt-oss-20b".ToModelId(GetIdentifier()), Name = "gpt-oss-20b", Type = "language", OwnedBy = "OpenAI" },

    // ===== SAO10K =====
    new() { Id = "Sao10K/L3-8B-Lunaris-v1".ToModelId(GetIdentifier()), Name = "L3-8B-Lunaris-v1", Type = "language", OwnedBy = "Sao10K" },
    new() { Id = "Sao10K/L3-8B-Stheno-v3.2".ToModelId(GetIdentifier()), Name = "L3-8B-Stheno-v3.2", Type = "language", OwnedBy = "Sao10K" },

    // ===== NOUS =====
    new() { Id = "NousResearch/Hermes-4-70B".ToModelId(GetIdentifier()), Name = "Hermes-4-70B", Type = "language", OwnedBy = "NousResearch" },

    // ===== XIAOMI =====
    new() { Id = "XiaomiMiMo/MiMo-V2-Flash".ToModelId(GetIdentifier()), Name = "MiMo-V2-Flash", Type = "language", OwnedBy = "Xiaomi" }
];


}