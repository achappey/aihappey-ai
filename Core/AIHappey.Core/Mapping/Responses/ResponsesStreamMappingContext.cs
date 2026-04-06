using System.Text;

namespace AIHappey.Core.AI;

public sealed class ResponsesStreamMappingContext
{
    public ResponsesStreamMappingContext(ResponsesStreamMappingOptions? options = null)
    {
        Options = options ?? new ResponsesStreamMappingOptions();
    }

    public ResponsesStreamMappingOptions Options { get; }

    internal Dictionary<string, PendingToolCall> PendingToolCalls { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, ShellCallState> ShellCalls { get; } = new(StringComparer.Ordinal);

    internal Dictionary<int, string> ShellCallIdsByOutputIndex { get; } = new();

    internal Dictionary<string, string> ShellCallIdsByItemId { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> StartedTextItemIds { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> TextDeltaItemIds { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> StartedReasoningItemIds { get; } = new(StringComparer.Ordinal);

    internal sealed class PendingToolCall
    {
        public string ItemId { get; init; } = default!;

        public string ToolCallId { get; set; } = default!;

        public string ToolName { get; set; } = default!;

        public StringBuilder Arguments { get; } = new();

        public bool ProviderExecuted { get; set; }

        public bool RequireApproval { get; set; }
    }

    internal sealed class ShellCallState
    {
        public string ToolCallId { get; set; } = default!;

        public string ToolName { get; set; } = "shell";

        public string? ShellCallItemId { get; set; }

        public string? ShellOutputItemId { get; set; }

        public int? ShellCallOutputIndex { get; set; }

        public int? ShellOutputIndex { get; set; }

        public bool StreamStarted { get; set; }

        public bool InputCompleted { get; set; }

        public bool OutputCompleted { get; set; }

        public bool InputJsonStarted { get; set; }

        public Dictionary<int, StringBuilder> CommandBuffers { get; } = new();

        public HashSet<int> StreamedCommandIndexes { get; } = new();

        public HashSet<int> CompletedCommandIndexes { get; } = new();

        public string? LastCommandPreview { get; set; }

        public string? LastOutputPreview { get; set; }
    }
}
