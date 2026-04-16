using System.Text;
using System.Text.Json;

namespace AIHappey.Messages.Mapping;

public static partial class MessagesUnifiedMapper
{
    private static readonly JsonSerializerOptions Json = MessagesJson.Default;

    private static string? NormalizeRequestModel(string? model, string? providerId)
    {
        var modelText = model?.Trim();
        if (string.IsNullOrWhiteSpace(modelText))
            return modelText;

        if (string.IsNullOrWhiteSpace(providerId))
            return modelText;

        var prefix = providerId.Trim() + "/";
        return modelText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? modelText[prefix.Length..]
            : modelText;
    }

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

        internal readonly HashSet<string> SeenSourceIds = new(StringComparer.OrdinalIgnoreCase);

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
            SeenSourceIds.Clear();
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

    public sealed class MessagesReverseStreamMappingState
    {
        internal readonly Dictionary<string, ReverseStreamBlockState> Blocks = new(StringComparer.Ordinal);

        internal readonly HashSet<string> EmittedRawParts = new(StringComparer.Ordinal);

        internal string MessageId { get; private set; } = $"msg_{Guid.NewGuid():N}";

        internal string? Model { get; private set; }

        internal string Role { get; private set; } = "assistant";

        internal bool MessageStarted { get; private set; }

        internal int NextBlockIndex { get; private set; }

        internal int? LastCitationBlockIndex { get; set; }

        internal bool TryMarkRawPart(string rawSignature)
            => EmittedRawParts.Add(rawSignature);

        internal void SetMessageContext(string? messageId, string? model, string? role)
        {
            if (!string.IsNullOrWhiteSpace(messageId))
                MessageId = messageId!;

            if (!string.IsNullOrWhiteSpace(model))
                Model = model;

            if (!string.IsNullOrWhiteSpace(role))
                Role = NormalizeRole(role) == "assistant" ? "assistant" : "assistant";
        }

        internal void MarkMessageStarted()
            => MessageStarted = true;

        internal ReverseStreamBlockState GetOrCreateBlock(string? eventId, string blockType)
        {
            var key = string.IsNullOrWhiteSpace(eventId)
                ? $"{blockType}:{NextBlockIndex}"
                : eventId!;

            if (Blocks.TryGetValue(key, out var existing))
                return existing;

            var created = new ReverseStreamBlockState(NextBlockIndex++, key, blockType);
            Blocks[key] = created;
            return created;
        }

        internal bool TryGetBlock(string? eventId, out ReverseStreamBlockState block)
        {
            if (!string.IsNullOrWhiteSpace(eventId) && Blocks.TryGetValue(eventId!, out block!))
                return true;

            block = null!;
            return false;
        }

        internal bool TryGetBlockByIndex(int index, out ReverseStreamBlockState block)
        {
            foreach (var candidate in Blocks.Values)
            {
                if (candidate.Index == index)
                {
                    block = candidate;
                    return true;
                }
            }

            block = null!;
            return false;
        }

        internal void Reset()
        {
            Blocks.Clear();
            EmittedRawParts.Clear();
            MessageId = $"msg_{Guid.NewGuid():N}";
            Model = null;
            Role = "assistant";
            MessageStarted = false;
            NextBlockIndex = 0;
            LastCitationBlockIndex = null;
        }
    }

    internal sealed class ReverseStreamBlockState
    {
        public ReverseStreamBlockState(int index, string key, string blockType)
        {
            Index = index;
            Key = key;
            BlockType = blockType;
        }

        public int Index { get; }

        public string Key { get; }

        public string BlockType { get; set; }

        public bool StartEmitted { get; set; }

        public bool StopEmitted { get; set; }

        public bool InputDeltaSeen { get; set; }

        public string? ToolName { get; set; }

        public string? Title { get; set; }

        public bool? ProviderExecuted { get; set; }

        public object? Input { get; set; }

        public string? Signature { get; set; }

        public string? EncryptedContent { get; set; }
    }
}
