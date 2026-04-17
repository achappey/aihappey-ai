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
    public JsonElement? Container { get; set; }

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

    [JsonPropertyName("context_management")]
    public object? ContextManagement { get; set; }

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("container")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessagesContainer? Container { get; set; }

    [JsonPropertyName("content")]
    public List<MessageContentBlock> Content { get; set; } = [];

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    [JsonPropertyName("stop_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessagesStopDetails? StopDetails { get; set; }

    [JsonPropertyName("stop_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopSequence { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessagesUsage? Usage { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Metadata { get; set; }
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("citations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MessageCitation>? Citations { get; set; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageSource? Source { get; set; }

    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CacheControlEphemeral? CacheControl { get; set; }

    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; set; }

    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Thinking { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Input { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("caller")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageCaller? Caller { get; set; }

    [JsonPropertyName("tool_use_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolUseId { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessagesContent? Content { get; set; }

    [JsonPropertyName("is_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsError { get; set; }

    [JsonPropertyName("enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; set; }

    [JsonPropertyName("file_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileId { get; set; }

    [JsonPropertyName("tool_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; set; }

    [JsonPropertyName("encrypted_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedContent { get; set; }

    [JsonPropertyName("encrypted_stdout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedStdout { get; set; }

    [JsonPropertyName("return_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReturnCode { get; set; }

    [JsonPropertyName("stderr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stderr { get; set; }

    [JsonPropertyName("stdout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stdout { get; set; }

    [JsonPropertyName("file_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileType { get; set; }

    [JsonPropertyName("num_lines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NumLines { get; set; }

    [JsonPropertyName("start_line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartLine { get; set; }

    [JsonPropertyName("total_lines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalLines { get; set; }

    [JsonPropertyName("is_file_update")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsFileUpdate { get; set; }

    [JsonPropertyName("lines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Lines { get; set; }

    [JsonPropertyName("new_lines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NewLines { get; set; }

    [JsonPropertyName("new_start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NewStart { get; set; }

    [JsonPropertyName("old_lines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OldLines { get; set; }

    [JsonPropertyName("old_start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OldStart { get; set; }

    [JsonPropertyName("error_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("error_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("retrieved_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RetrievedAt { get; set; }

    [JsonPropertyName("page_age")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PageAge { get; set; }

    [JsonPropertyName("encrypted_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedIndex { get; set; }

    [JsonPropertyName("cited_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CitedText { get; set; }

    [JsonPropertyName("document_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DocumentIndex { get; set; }

    [JsonPropertyName("document_title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentTitle { get; set; }

    [JsonPropertyName("start_char_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartCharIndex { get; set; }

    [JsonPropertyName("end_char_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EndCharIndex { get; set; }

    [JsonPropertyName("start_page_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartPageNumber { get; set; }

    [JsonPropertyName("end_page_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EndPageNumber { get; set; }

    [JsonPropertyName("start_block_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartBlockIndex { get; set; }

    [JsonPropertyName("end_block_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EndBlockIndex { get; set; }

    [JsonPropertyName("search_result_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CitedText { get; set; }

    [JsonPropertyName("document_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DocumentIndex { get; set; }

    [JsonPropertyName("document_title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentTitle { get; set; }

    [JsonPropertyName("start_char_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartCharIndex { get; set; }

    [JsonPropertyName("end_char_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EndCharIndex { get; set; }

    [JsonPropertyName("start_page_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartPageNumber { get; set; }

    [JsonPropertyName("end_page_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EndPageNumber { get; set; }

    [JsonPropertyName("start_block_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartBlockIndex { get; set; }

    [JsonPropertyName("end_block_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EndBlockIndex { get; set; }

    [JsonPropertyName("encrypted_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedIndex { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("search_result_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SearchResultIndex { get; set; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    [JsonPropertyName("file_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessageSource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }

    [JsonPropertyName("media_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaType { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessagesContent? Content { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class CacheControlEphemeral
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "ephemeral";

    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("expires_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpiresAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessagesStopDetails
{
    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    [JsonPropertyName("explanation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Explanation { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessagesUsage
{
    [JsonPropertyName("cache_creation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessagesCacheCreation? CacheCreation { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CacheCreationInputTokens { get; set; }

    [JsonPropertyName("cache_read_input_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CacheReadInputTokens { get; set; }

    [JsonPropertyName("inference_geo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InferenceGeo { get; set; }

    [JsonPropertyName("input_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputTokens { get; set; }

    [JsonPropertyName("server_tool_use")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessagesServerToolUsage? ServerToolUse { get; set; }

    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessagesCacheCreation
{
    [JsonPropertyName("ephemeral_1h_input_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Ephemeral1hInputTokens { get; set; }

    [JsonPropertyName("ephemeral_5m_input_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Ephemeral5mInputTokens { get; set; }
}

public sealed class MessagesServerToolUsage
{
    [JsonPropertyName("web_fetch_requests")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WebFetchRequests { get; set; }

    [JsonPropertyName("web_search_requests")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WebSearchRequests { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessageStreamPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessagesResponse? Message { get; set; }

    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Index { get; set; }

    [JsonPropertyName("content_block")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageContentBlock? ContentBlock { get; set; }

    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageStreamDelta? Delta { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessagesUsage? Usage { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageStreamError? Error { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Metadata { get; set; }

}

public sealed class MessageStreamDelta
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Thinking { get; set; }

    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; set; }

    [JsonPropertyName("partial_json")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartialJson { get; set; }

    [JsonPropertyName("citation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageCitation? Citation { get; set; }

    [JsonPropertyName("stop_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopSequence { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class MessageStreamError
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
