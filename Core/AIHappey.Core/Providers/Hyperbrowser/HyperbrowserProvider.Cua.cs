using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Hyperbrowser;

public partial class HyperbrowserProvider
{
    private static readonly HyperbrowserTaskDefinition CuaTaskDefinition = new(
        Kind: "cua",
        Endpoint: "task/cua",
        ToolName: "hyperbrowser_cua_task",
        ToolTitle: "Hyperbrowser CUA task",
        DisplayName: "CUA",
        DefaultLlm: "computer-use-preview",
        DefaultVersion: string.Empty,
        SupportedLlms:
        [
            "computer-use-preview",
            "gpt-5.4",
            "gpt-5.4-mini"
        ],
        ApplySpecificPayload: ApplyCuaPayload,
        CreateStepOutputItems: CreateCuaStepOutputItems,
        CreateStepStreamEvents: CreateCuaStepStreamEvents);

    private static void ApplyCuaPayload(Dictionary<string, object?> payload, HyperbrowserTaskMetadata metadata)
        => ApplyComputerUsePayload(payload, metadata);

    private static IEnumerable<AIOutputItem> CreateCuaStepOutputItems(
        HyperbrowserTaskDefinition definition,
        HyperbrowserTaskStartResponse created,
        HyperbrowserTaskResponse task)
    {
        yield break;
    }

    private static IEnumerable<AIStreamEvent> CreateCuaStepStreamEvents(
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

