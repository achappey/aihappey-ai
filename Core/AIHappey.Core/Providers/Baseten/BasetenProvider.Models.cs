using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Baseten;

public sealed partial class BasetenProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        return await Task.FromResult<IEnumerable<Model>>(BasetenLanguageModels);
    }

    // Hardcoded to keep onboarding simple.
    private IReadOnlyList<Model> BasetenLanguageModels =>
    [
        NewLanguageModel("zai-org/GLM-4.6"),
        NewLanguageModel("zai-org/GLM-4.7"),
        NewLanguageModel("moonshotai/Kimi-K2-Thinking"),
        NewLanguageModel("deepseek-ai/DeepSeek-V3-0324"),
        NewLanguageModel("moonshotai/Kimi-K2-Instruct-0905"),
        NewLanguageModel("openai/gpt-oss-120b"),
        NewLanguageModel("deepseek-ai/DeepSeek-V3.1"),
        NewLanguageModel("deepseek-ai/DeepSeek-V3.2"),
        NewLanguageModel("Qwen/Qwen3-Coder-480B-A35B-Instruct"),
    ];

    private Model NewLanguageModel(string modelId) => new()
    {
        Id = modelId.ToModelId(GetIdentifier()),
        Name = modelId,
        Description = modelId,
        Type = "language",
        OwnedBy = nameof(Baseten)
    };
}

