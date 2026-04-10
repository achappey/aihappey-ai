using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Messages.Mapping;

public static partial class MessagesUnifiedMapper
{
    private static readonly JsonSerializerOptions Json = MessagesJson.Default;

    private static readonly HashSet<string> MappedRequestFields =
    [
        "model",
        "max_tokens",
        "messages",
        "cache_control",
        "container",
        "inference_geo",
        "metadata",
        "output_config",
        "service_tier",
        "stop_sequences",
        "stream",
        "system",
        "temperature",
        "thinking",
        "tool_choice",
        "tools",
        "top_k",
        "top_p"
    ];

    public sealed class MessagesStreamMappingState
    {
        internal readonly Dictionary<int, StreamBlockState> Blocks = new();

        internal MessagesResponse? CurrentMessage { get; set; }

        internal MessagesUsage? Usage { get; set; }

        internal string? StopReason { get; set; }

        internal string? StopSequence { get; set; }

        internal string? ActiveTextEventId { get; private set; }

        internal bool ActiveTextStarted { get; private set; }

        internal StreamBlockState GetOrCreate(int index, MessageContentBlock block, string? messageId)
        {
            if (Blocks.TryGetValue(index, out var existing))
                return existing;

            var eventId = ResolveStreamEventId(block, $"{messageId ?? "msg"}:{index}:{block.Type}");
            var created = new StreamBlockState(index, eventId, block);
            Blocks[index] = created;
            return created;
        }

        internal string EnsureActiveTextEventId(string eventId)
        {
            if (string.IsNullOrWhiteSpace(ActiveTextEventId))
                ActiveTextEventId = eventId;

            return ActiveTextEventId;
        }

        internal bool MarkActiveTextStarted()
        {
            if (string.IsNullOrWhiteSpace(ActiveTextEventId) || ActiveTextStarted)
                return false;

            ActiveTextStarted = true;
            return true;
        }

        internal string? CloseActiveTextSpan()
        {
            var eventId = ActiveTextStarted ? ActiveTextEventId : null;

            ActiveTextEventId = null;
            ActiveTextStarted = false;

            return eventId;
        }

        internal void Reset()
        {
            Blocks.Clear();
            CurrentMessage = null;
            Usage = null;
            StopReason = null;
            StopSequence = null;
            ActiveTextEventId = null;
            ActiveTextStarted = false;
        }
    }

    internal sealed class StreamBlockState
    {
        public StreamBlockState(int index, string eventId, MessageContentBlock block)
        {
            Index = index;
            EventId = eventId;
            Block = block;
            BlockType = block.Type;
        }

        public int Index { get; }

        public string EventId { get; }

        public string BlockType { get; }

        public MessageContentBlock Block { get; }

        public StringBuilder InputJson { get; } = new();

        public string? Signature { get; set; }
    }
}
