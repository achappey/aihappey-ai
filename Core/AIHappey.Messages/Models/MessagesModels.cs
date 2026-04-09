using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Messages;

public sealed class MessagesRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("messages")]
    public List<MessageParam> Messages { get; set; } = [];

    [JsonPropertyName("cache_control")]
    public CacheControlEphemeral? CacheControl { get; set; }

    [JsonPropertyName("container")]
    public string? Container { get; set; }

    [JsonPropertyName("inference_geo")]
    public string? InferenceGeo { get; set; }

    [JsonPropertyName("metadata")]
    public MessagesRequestMetadata? Metadata { get; set; }

    [JsonPropertyName("output_config")]
    public MessagesOutputConfig? OutputConfig { get; set; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("stop_sequences")]
    public List<string>? StopSequences { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("system")]
    public MessagesContent? System { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("thinking")]
    public MessagesThinkingConfig? Thinking { get; set; }

    [JsonPropertyName("tool_choice")]
    public MessageToolChoice? ToolChoice { get; set; }

    [JsonPropertyName("tools")]
    public List<MessageToolDefinition>? Tools { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessagesResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("container")]
    public MessagesContainer? Container { get; set; }

    [JsonPropertyName("content")]
    public List<MessageContentBlock> Content { get; set; } = [];

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("stop_details")]
    public MessagesStopDetails? StopDetails { get; set; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("usage")]
    public MessagesUsage? Usage { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessageParam
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public MessagesContent Content { get; set; } = new(string.Empty);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

[JsonConverter(typeof(MessagesContentJsonConverter))]
public sealed class MessagesContent
{
    public MessagesContent()
    {
    }

    public MessagesContent(string text)
    {
        Text = text;
    }

    public MessagesContent(List<MessageContentBlock> blocks)
    {
        Blocks = blocks;
    }

    public MessagesContent(JsonElement raw)
    {
        Raw = raw;
    }

    public string? Text { get; init; }

    public List<MessageContentBlock>? Blocks { get; init; }

    public JsonElement? Raw { get; init; }

    public bool IsText => Text is not null;

    public bool IsBlocks => Blocks is not null;

    public bool IsRaw => Raw is not null && Raw.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
}

public sealed class MessageContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("citations")]
    public List<MessageCitation>? Citations { get; set; }

    [JsonPropertyName("source")]
    public MessageSource? Source { get; set; }

    [JsonPropertyName("cache_control")]
    public CacheControlEphemeral? CacheControl { get; set; }

    [JsonPropertyName("context")]
    public string? Context { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("input")]
    public JsonElement? Input { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("caller")]
    public MessageCaller? Caller { get; set; }

    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; set; }

    [JsonPropertyName("content")]
    public MessagesContent? Content { get; set; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; set; }

    [JsonPropertyName("encrypted_content")]
    public string? EncryptedContent { get; set; }

    [JsonPropertyName("encrypted_stdout")]
    public string? EncryptedStdout { get; set; }

    [JsonPropertyName("return_code")]
    public int? ReturnCode { get; set; }

    [JsonPropertyName("stderr")]
    public string? Stderr { get; set; }

    [JsonPropertyName("stdout")]
    public string? Stdout { get; set; }

    [JsonPropertyName("file_type")]
    public string? FileType { get; set; }

    [JsonPropertyName("num_lines")]
    public int? NumLines { get; set; }

    [JsonPropertyName("start_line")]
    public int? StartLine { get; set; }

    [JsonPropertyName("total_lines")]
    public int? TotalLines { get; set; }

    [JsonPropertyName("is_file_update")]
    public bool? IsFileUpdate { get; set; }

    [JsonPropertyName("lines")]
    public List<string>? Lines { get; set; }

    [JsonPropertyName("new_lines")]
    public int? NewLines { get; set; }

    [JsonPropertyName("new_start")]
    public int? NewStart { get; set; }

    [JsonPropertyName("old_lines")]
    public int? OldLines { get; set; }

    [JsonPropertyName("old_start")]
    public int? OldStart { get; set; }

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("retrieved_at")]
    public string? RetrievedAt { get; set; }

    [JsonPropertyName("page_age")]
    public string? PageAge { get; set; }

    [JsonPropertyName("encrypted_index")]
    public string? EncryptedIndex { get; set; }

    [JsonPropertyName("cited_text")]
    public string? CitedText { get; set; }

    [JsonPropertyName("document_index")]
    public int? DocumentIndex { get; set; }

    [JsonPropertyName("document_title")]
    public string? DocumentTitle { get; set; }

    [JsonPropertyName("start_char_index")]
    public int? StartCharIndex { get; set; }

    [JsonPropertyName("end_char_index")]
    public int? EndCharIndex { get; set; }

    [JsonPropertyName("start_page_number")]
    public int? StartPageNumber { get; set; }

    [JsonPropertyName("end_page_number")]
    public int? EndPageNumber { get; set; }

    [JsonPropertyName("start_block_index")]
    public int? StartBlockIndex { get; set; }

    [JsonPropertyName("end_block_index")]
    public int? EndBlockIndex { get; set; }

    [JsonPropertyName("search_result_index")]
    public int? SearchResultIndex { get; set; }

    [JsonIgnore]
    public string? CitationSource { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessageCitation
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("cited_text")]
    public string? CitedText { get; set; }

    [JsonPropertyName("document_index")]
    public int? DocumentIndex { get; set; }

    [JsonPropertyName("document_title")]
    public string? DocumentTitle { get; set; }

    [JsonPropertyName("start_char_index")]
    public int? StartCharIndex { get; set; }

    [JsonPropertyName("end_char_index")]
    public int? EndCharIndex { get; set; }

    [JsonPropertyName("start_page_number")]
    public int? StartPageNumber { get; set; }

    [JsonPropertyName("end_page_number")]
    public int? EndPageNumber { get; set; }

    [JsonPropertyName("start_block_index")]
    public int? StartBlockIndex { get; set; }

    [JsonPropertyName("end_block_index")]
    public int? EndBlockIndex { get; set; }

    [JsonPropertyName("encrypted_index")]
    public string? EncryptedIndex { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("search_result_index")]
    public int? SearchResultIndex { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessageSource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("content")]
    public MessagesContent? Content { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class CacheControlEphemeral
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "ephemeral";

    [JsonPropertyName("ttl")]
    public string? Ttl { get; set; }
}

public sealed class MessageCaller
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("tool_id")]
    public string? ToolId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessageToolChoice
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "auto";

    [JsonPropertyName("disable_parallel_tool_use")]
    public bool? DisableParallelToolUse { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessageToolDefinition
{
    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("allowed_callers")]
    public List<string>? AllowedCallers { get; set; }

    [JsonPropertyName("cache_control")]
    public CacheControlEphemeral? CacheControl { get; set; }

    [JsonPropertyName("defer_loading")]
    public bool? DeferLoading { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("eager_input_streaming")]
    public bool? EagerInputStreaming { get; set; }

    [JsonPropertyName("input_examples")]
    public List<JsonElement>? InputExamples { get; set; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("allowed_domains")]
    public List<string>? AllowedDomains { get; set; }

    [JsonPropertyName("blocked_domains")]
    public List<string>? BlockedDomains { get; set; }

    [JsonPropertyName("max_uses")]
    public int? MaxUses { get; set; }

    [JsonPropertyName("max_content_tokens")]
    public int? MaxContentTokens { get; set; }

    [JsonPropertyName("max_characters")]
    public int? MaxCharacters { get; set; }

    [JsonPropertyName("use_cache")]
    public bool? UseCache { get; set; }

    [JsonPropertyName("citations")]
    public CitationsConfigParam? Citations { get; set; }

    [JsonPropertyName("user_location")]
    public MessageUserLocation? UserLocation { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessageUserLocation
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessagesRequestMetadata
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessagesOutputConfig
{
    [JsonPropertyName("effort")]
    public string? Effort { get; set; }

    [JsonPropertyName("format")]
    public MessagesOutputFormat? Format { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessagesOutputFormat
{
    [JsonPropertyName("schema")]
    public JsonElement? Schema { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessagesThinkingConfig
{
    [JsonPropertyName("budget_tokens")]
    public int? BudgetTokens { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "disabled";

    [JsonPropertyName("display")]
    public string? Display { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class CitationsConfigParam
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessagesContainer
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessagesStopDetails
{
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("explanation")]
    public string? Explanation { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessagesUsage
{
    [JsonPropertyName("cache_creation")]
    public MessagesCacheCreation? CacheCreation { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int? CacheCreationInputTokens { get; set; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int? CacheReadInputTokens { get; set; }

    [JsonPropertyName("inference_geo")]
    public string? InferenceGeo { get; set; }

    [JsonPropertyName("input_tokens")]
    public int? InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int? OutputTokens { get; set; }

    [JsonPropertyName("server_tool_use")]
    public MessagesServerToolUsage? ServerToolUse { get; set; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessagesCacheCreation
{
    [JsonPropertyName("ephemeral_1h_input_tokens")]
    public int? Ephemeral1hInputTokens { get; set; }

    [JsonPropertyName("ephemeral_5m_input_tokens")]
    public int? Ephemeral5mInputTokens { get; set; }
}

public sealed class MessagesServerToolUsage
{
    [JsonPropertyName("web_fetch_requests")]
    public int? WebFetchRequests { get; set; }

    [JsonPropertyName("web_search_requests")]
    public int? WebSearchRequests { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessageStreamPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("message")]
    public MessagesResponse? Message { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("content_block")]
    public MessageContentBlock? ContentBlock { get; set; }

    [JsonPropertyName("delta")]
    public MessageStreamDelta? Delta { get; set; }

    [JsonPropertyName("usage")]
    public MessagesUsage? Usage { get; set; }

    [JsonPropertyName("error")]
    public MessageStreamError? Error { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessageStreamDelta
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("partial_json")]
    public string? PartialJson { get; set; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessageStreamError
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public static class MessagesJson
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new MessagesContentJsonConverter()
        }
    };
}
