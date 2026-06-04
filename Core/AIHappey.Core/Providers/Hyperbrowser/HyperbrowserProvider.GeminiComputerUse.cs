using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Hyperbrowser;

public partial class HyperbrowserProvider
{
    private static readonly HyperbrowserTaskDefinition GeminiComputerUseTaskDefinition = new(
        Kind: "gemini-computer-use",
        Endpoint: "task/gemini-computer-use",
        ToolName: "hyperbrowser_gemini_computer_use_task",
        ToolTitle: "Hyperbrowser Gemini Computer Use task",
        DisplayName: "Gemini Computer Use",
        DefaultLlm: "gemini-3-flash-preview",
        DefaultVersion: string.Empty,
        SupportedLlms:
        [
            "gemini-3-flash-preview",
            "gemini-2.5-computer-use-preview-10-2025"
        ],
        ApplySpecificPayload: ApplyGeminiComputerUsePayload,
        CreateStepOutputItems: CreateGeminiComputerUseStepOutputItems,
        CreateStepStreamEvents: CreateGeminiComputerUseStepStreamEvents);

    private static void ApplyGeminiComputerUsePayload(Dictionary<string, object?> payload, HyperbrowserTaskMetadata metadata)
        => ApplyComputerUsePayload(payload, metadata);

    private static IEnumerable<AIOutputItem> CreateGeminiComputerUseStepOutputItems(
        HyperbrowserTaskDefinition definition,
        HyperbrowserTaskStartResponse created,
        HyperbrowserTaskResponse task)
    {
        yield break;
    }

    private static IEnumerable<AIStreamEvent> CreateGeminiComputerUseStepStreamEvents(
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

