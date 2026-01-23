using AIHappey.Core.Models;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Friendli;

public partial class FriendliProvider : IModelProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return FriendliLanguageModels;
    }

    public static IReadOnlyList<Model> FriendliLanguageModels =>
 [
     new()
    {
        Id = "friendli/zai-org/GLM-4.7",
        Name = "GLM-4.7",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.6m, Output = 2.2m }
    },
    new()
    {
        Id = "friendli/zai-org/GLM-4.6",
        Name = "GLM-4.6",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.004m, Output = 0.004m }
    },
    new()
    {
        Id = "friendli/LGAI-EXAONE/K-EXAONE-236B-A23B",
        Name = "K-EXAONE-236B-A23B",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0m, Output = 0m } // Free (until Jan 28)
    },
    new()
    {
        Id = "friendli/meta-llama/Llama-3.1-8B-Instruct",
        Name = "Llama-3.1-8B-Instruct",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.1m, Output = 0.1m }
    },
    new()
    {
        Id = "friendli/mistralai/Magistral-Small-2506",
        Name = "Magistral-Small-2506",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.002m, Output = 0.002m }
    },
    new()
    {
        Id = "friendli/skt/A.X-3.1",
        Name = "A.X-3.1",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.002m, Output = 0.002m }
    },
    new()
    {
        Id = "friendli/Qwen/Qwen3-235B-A22B-Thinking-2507",
        Name = "Qwen3-235B-A22B-Thinking-2507",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.004m, Output = 0.004m }
    },
    new()
    {
        Id = "friendli/Qwen/Qwen3-235B-A22B-Instruct-2507",
        Name = "Qwen3-235B-A22B-Instruct-2507",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.2m, Output = 0.8m }
    },
    new()
    {
        Id = "friendli/meta-llama/Llama-3.3-70B-Instruct",
        Name = "Llama-3.3-70B-Instruct",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.6m, Output = 0.6m }
    },
    new()
    {
        Id = "friendli/mistralai/Devstral-Small-2505",
        Name = "Devstral-Small-2505",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.002m, Output = 0.002m }
    },
    new()
    {
        Id = "friendli/google/gemma-3-27b-it",
        Name = "gemma-3-27b-it",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.002m, Output = 0.002m }
    },
    new()
    {
        Id = "friendli/Qwen/Qwen3-32B",
        Name = "Qwen3-32B",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.002m, Output = 0.002m }
    },
    new()
    {
        Id = "friendli/meta-llama/Llama-4-Scout-17B-16E-Instruct",
        Name = "Llama-4-Scout-17B-16E-Instruct",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.002m, Output = 0.002m }
    },
    new()
    {
        Id = "friendli/Qwen/Qwen3-30B-A3B",
        Name = "Qwen3-30B-A3B",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.002m, Output = 0.002m }
    },
    new()
    {
        Id = "friendli/meta-llama/Llama-4-Maverick-17B-128E-Instruct",
        Name = "Llama-4-Maverick-17B-128E-Instruct",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.004m, Output = 0.004m }
    },
    new()
    {
        Id = "friendli/mistralai/Mistral-Small-3.1-24B-Instruct-2503",
        Name = "Mistral-Small-3.1-24B-Instruct-2503",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.002m, Output = 0.002m }
    },
    new()
    {
        Id = "friendli/deepseek-ai/DeepSeek-V3.1",
        Name = "DeepSeek-V3.1",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.004m, Output = 0.004m }
    },
    new()
    {
        Id = "friendli/MiniMaxAI/MiniMax-M2.1",
        Name = "MiniMax-M2.1",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.3m, Output = 1.2m }
    },
    new()
    {
        Id = "friendli/skt/A.X-4.0",
        Name = "A.X-4.0",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.002m, Output = 0.002m }
    },
    new()
    {
        Id = "friendli/naver-hyperclovax/HyperCLOVAX-SEED-Think-14B",
        Name = "HyperCLOVAX-SEED-Think-14B",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = null // pricing not listed
    },
    new()
    {
        Id = "friendli/LGAI-EXAONE/EXAONE-4.0.1-32B",
        Name = "EXAONE-4.0.1-32B",
        Type = "language",
        OwnedBy = "Friendli",
        Pricing = new() { Input = 0.6m, Output = 1.0m }
    }
 ];


}