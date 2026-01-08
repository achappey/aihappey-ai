using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.CanopyWave;

public partial class CanopyWaveProvider : IModelProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        // Hardcoded for now; CanopyWave also supports an OpenAI-compatible models endpoint,
        // but we keep this provider minimal.
        return Task.FromResult<IEnumerable<Model>>(CanopyWaveLanguageModels);
    }

    public static IReadOnlyList<Model> CanopyWaveLanguageModels =>
    [
        new()
        {
            Id = "qwen/qwen3-coder".ToModelId("canopywave"),
            Name = "qwen3-coder",
            Type = "language",
            OwnedBy = "CanopyWave"
        },
        new()
        {
            Id = "moonshotai/kimi-k2-thinking".ToModelId("canopywave"),
            Name = "kimi-k2-thinking",
            Type = "language",
            OwnedBy = "CanopyWave"
        },
        new()
        {
            Id = "deepseek/deepseek-chat-v3.2".ToModelId("canopywave"),
            Name = "deepseek-chat-v3.2",
            Type = "language",
            OwnedBy = "CanopyWave"
        },
        new()
        {
            Id = "deepseek-ai/DeepSeek-R1-Distill-Qwen-32B".ToModelId("canopywave"),
            Name = "DeepSeek-R1-Distill-Qwen-32B",
            Type = "language",
            OwnedBy = "CanopyWave"
        },
        new()
        {
            Id = "deepseek-ai/DeepSeek-Math-V2".ToModelId("canopywave"),
            Name = "DeepSeek-Math-V2",
            Type = "language",
            OwnedBy = "CanopyWave"
        },
        new()
        {
            Id = "minimax/minimax-m2.1".ToModelId("canopywave"),
            Name = "minimax-m2.1",
            Type = "language",
            OwnedBy = "CanopyWave"
        },
        new()
        {
            Id = "openai/gpt-oss-120b".ToModelId("canopywave"),
            Name = "gpt-oss-120b",
            Type = "language",
            OwnedBy = "CanopyWave"
        },
    ];
}

