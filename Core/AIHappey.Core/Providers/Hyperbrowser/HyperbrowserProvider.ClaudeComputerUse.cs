using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Hyperbrowser;

public partial class HyperbrowserProvider
{
    private static readonly HyperbrowserTaskDefinition ClaudeComputerUseTaskDefinition = new(
        Kind: "claude-computer-use",
        Endpoint: "task/claude-computer-use",
        ToolName: "hyperbrowser_claude_computer_use_task",
        ToolTitle: "Hyperbrowser Claude Computer Use task",
        DisplayName: "Claude Computer Use",
        DefaultLlm: "claude-opus-4-8",
        DefaultVersion: string.Empty,
        SupportedLlms:
        [
            "claude-opus-4-5",
            "claude-opus-4-6",
            "claude-opus-4-7",
            "claude-opus-4-8",
            "claude-haiku-4-5-20251001",
            "claude-sonnet-4-6",
            "claude-sonnet-4-5",
            "claude-sonnet-4-20250514"
        ],
        ApplySpecificPayload: ApplyClaudeComputerUsePayload,
        CreateStepOutputItems: CreateClaudeComputerUseStepOutputItems,
        CreateStepStreamEvents: CreateClaudeComputerUseStepStreamEvents);

    private static void ApplyClaudeComputerUsePayload(Dictionary<string, object?> payload, HyperbrowserTaskMetadata metadata)
        => ApplyComputerUsePayload(payload, metadata);

    private static IEnumerable<AIOutputItem> CreateClaudeComputerUseStepOutputItems(
        HyperbrowserTaskDefinition definition,
        HyperbrowserTaskStartResponse created,
        HyperbrowserTaskResponse task)
    {
        yield break;
    }

    private static IEnumerable<AIStreamEvent> CreateClaudeComputerUseStepStreamEvents(
        string providerId,
        string eventId,
        HyperbrowserTaskDefinition definition,
        HyperbrowserTaskResponse task,
        Dictionary<string, object?>? metadata,
        HashSet<int> emittedStepIndexes)
    {
        yield break;
    }
}

