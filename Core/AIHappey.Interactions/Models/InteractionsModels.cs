using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Interactions;

public sealed class InteractionRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    [JsonPropertyName("input")]
    public InteractionsInput? Input { get; set; }

    [JsonPropertyName("system_instruction")]
    public string? SystemInstruction { get; set; }

    [JsonPropertyName("tools")]
    public List<InteractionTool>? Tools { get; set; }

    [JsonPropertyName("response_format")]
    public object? ResponseFormat { get; set; }

    [JsonPropertyName("response_mime_type")]
    public string? ResponseMimeType { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("store")]
    public bool? Store { get; set; }

    [JsonPropertyName("background")]
    public bool? Background { get; set; }

    [JsonPropertyName("generation_config")]
    public InteractionGenerationConfig? GenerationConfig { get; set; }

    [JsonPropertyName("agent_config")]
    public InteractionAgentConfig? AgentConfig { get; set; }

    [JsonPropertyName("previous_interaction_id")]
    public string? PreviousInteractionId { get; set; }

    [JsonPropertyName("response_modalities")]
    public List<string>? ResponseModalities { get; set; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class Interaction
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("created")]
    public string? Created { get; set; }

    [JsonPropertyName("updated")]
    public string? Updated { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("outputs")]
    public List<InteractionContent>? Outputs { get; set; }

    [JsonPropertyName("system_instruction")]
    public string? SystemInstruction { get; set; }

    [JsonPropertyName("tools")]
    public List<InteractionTool>? Tools { get; set; }

    [JsonPropertyName("usage")]
    public InteractionUsage? Usage { get; set; }

    [JsonPropertyName("response_modalities")]
    public List<string>? ResponseModalities { get; set; }

    [JsonPropertyName("response_format")]
    public object? ResponseFormat { get; set; }

    [JsonPropertyName("response_mime_type")]
    public string? ResponseMimeType { get; set; }

    [JsonPropertyName("previous_interaction_id")]
    public string? PreviousInteractionId { get; set; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("input")]
    public InteractionsInput? Input { get; set; }

    [JsonPropertyName("generation_config")]
    public InteractionGenerationConfig? GenerationConfig { get; set; }

    [JsonPropertyName("agent_config")]
    public InteractionAgentConfig? AgentConfig { get; set; }

    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

[JsonConverter(typeof(InteractionsInputJsonConverter))]
public sealed class InteractionsInput
{
    public InteractionsInput()
    {
    }

    public InteractionsInput(string text)
    {
        Text = text;
    }

    public InteractionsInput(List<InteractionContent> content)
    {
        Content = content;
    }

    public InteractionsInput(List<InteractionTurn> turns, bool useTurns)
    {
        Turns = turns;
    }

    public InteractionsInput(InteractionContent singleContent)
    {
        SingleContent = singleContent;
    }

    public InteractionsInput(JsonElement raw)
    {
        Raw = raw;
    }

    public string? Text { get; init; }

    public InteractionContent? SingleContent { get; init; }

    public List<InteractionContent>? Content { get; init; }

    public List<InteractionTurn>? Turns { get; init; }

    public JsonElement? Raw { get; init; }

    public bool IsText => Text is not null;

    public bool IsSingleContent => SingleContent is not null;

    public bool IsContent => Content is not null;

    public bool IsTurns => Turns is not null;

    public bool IsRaw => Raw is not null && Raw.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
}

public sealed class InteractionTurn
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public InteractionTurnContent? Content { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

[JsonConverter(typeof(InteractionTurnContentJsonConverter))]
public sealed class InteractionTurnContent
{
    public InteractionTurnContent()
    {
    }

    public InteractionTurnContent(string text)
    {
        Text = text;
    }

    public InteractionTurnContent(List<InteractionContent> parts)
    {
        Parts = parts;
    }

    public InteractionTurnContent(JsonElement raw)
    {
        Raw = raw;
    }

    public string? Text { get; init; }

    public List<InteractionContent>? Parts { get; init; }

    public JsonElement? Raw { get; init; }

    public bool IsText => Text is not null;

    public bool IsParts => Parts is not null;

    public bool IsRaw => Raw is not null && Raw.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
}

[JsonConverter(typeof(InteractionContentJsonConverter))]
public abstract class InteractionContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionTextContent : InteractionContent
{
    public InteractionTextContent()
    {
        Type = "text";
    }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("annotations")]
    public List<InteractionAnnotation>? Annotations { get; set; }
}

public sealed class InteractionImageContent : InteractionContent
{
    public InteractionImageContent()
    {
        Type = "image";
    }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }
}

public sealed class InteractionAudioContent : InteractionContent
{
    public InteractionAudioContent()
    {
        Type = "audio";
    }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("rate")]
    public int? Rate { get; set; }

    [JsonPropertyName("channels")]
    public int? Channels { get; set; }
}

public sealed class InteractionDocumentContent : InteractionContent
{
    public InteractionDocumentContent()
    {
        Type = "document";
    }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }
}

public sealed class InteractionVideoContent : InteractionContent
{
    public InteractionVideoContent()
    {
        Type = "video";
    }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }
}

public sealed class InteractionThoughtContent : InteractionContent
{
    public InteractionThoughtContent()
    {
        Type = "thought";
    }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("summary")]
    public List<InteractionContent>? Summary { get; set; }
}

public sealed class InteractionFunctionCallContent : InteractionContent
{
    public InteractionFunctionCallContent()
    {
        Type = "function_call";
    }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public object? Arguments { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

public sealed class InteractionCodeExecutionCallContent : InteractionContent
{
    public InteractionCodeExecutionCallContent()
    {
        Type = "code_execution_call";
    }

    [JsonPropertyName("arguments")]
    public InteractionCodeExecutionCallArguments? Arguments { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

public sealed class InteractionUrlContextCallContent : InteractionContent
{
    public InteractionUrlContextCallContent()
    {
        Type = "url_context_call";
    }

    [JsonPropertyName("arguments")]
    public InteractionUrlContextCallArguments? Arguments { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

public sealed class InteractionMcpServerToolCallContent : InteractionContent
{
    public InteractionMcpServerToolCallContent()
    {
        Type = "mcp_server_tool_call";
    }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("server_name")]
    public string? ServerName { get; set; }

    [JsonPropertyName("arguments")]
    public object? Arguments { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

public sealed class InteractionGoogleSearchCallContent : InteractionContent
{
    public InteractionGoogleSearchCallContent()
    {
        Type = "google_search_call";
    }

    [JsonPropertyName("arguments")]
    public InteractionGoogleSearchCallArguments? Arguments { get; set; }

    [JsonPropertyName("search_type")]
    public string? SearchType { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

public sealed class InteractionFileSearchCallContent : InteractionContent
{
    public InteractionFileSearchCallContent()
    {
        Type = "file_search_call";
    }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

public sealed class InteractionGoogleMapsCallContent : InteractionContent
{
    public InteractionGoogleMapsCallContent()
    {
        Type = "google_maps_call";
    }

    [JsonPropertyName("arguments")]
    public InteractionGoogleMapsCallArguments? Arguments { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

public sealed class InteractionFunctionResultContent : InteractionContent
{
    public InteractionFunctionResultContent()
    {
        Type = "function_result";
    }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }
}

public sealed class InteractionCodeExecutionResultContent : InteractionContent
{
    public InteractionCodeExecutionResultContent()
    {
        Type = "code_execution_result";
    }

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

public sealed class InteractionUrlContextResultContent : InteractionContent
{
    public InteractionUrlContextResultContent()
    {
        Type = "url_context_result";
    }

    [JsonPropertyName("result")]
    public List<InteractionUrlContextResult>? Result { get; set; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

public sealed class InteractionGoogleSearchResultContent : InteractionContent
{
    public InteractionGoogleSearchResultContent()
    {
        Type = "google_search_result";
    }

    [JsonPropertyName("result")]
    public List<InteractionGoogleSearchResult>? Result { get; set; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

public sealed class InteractionMcpServerToolResultContent : InteractionContent
{
    public InteractionMcpServerToolResultContent()
    {
        Type = "mcp_server_tool_result";
    }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("server_name")]
    public string? ServerName { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }
}

public sealed class InteractionFileSearchResultContent : InteractionContent
{
    public InteractionFileSearchResultContent()
    {
        Type = "file_search_result";
    }

    [JsonPropertyName("result")]
    public List<InteractionFileSearchResult>? Result { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

public sealed class InteractionGoogleMapsResultContent : InteractionContent
{
    public InteractionGoogleMapsResultContent()
    {
        Type = "google_maps_result";
    }

    [JsonPropertyName("result")]
    public List<InteractionGoogleMapsResult>? Result { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

public sealed class InteractionAnnotation
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("start_index")]
    public int? StartIndex { get; set; }

    [JsonPropertyName("end_index")]
    public int? EndIndex { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionCodeExecutionCallArguments
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionUrlContextCallArguments
{
    [JsonPropertyName("urls")]
    public List<string>? Urls { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionGoogleSearchCallArguments
{
    [JsonPropertyName("queries")]
    public List<string>? Queries { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionGoogleMapsCallArguments
{
    [JsonPropertyName("queries")]
    public List<string>? Queries { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionUrlContextResult
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionGoogleSearchResult
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("search_suggestions")]
    public string? SearchSuggestions { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionFileSearchResult
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("file_search_store")]
    public string? FileSearchStore { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionGoogleMapsResult
{
    [JsonPropertyName("places")]
    public List<InteractionPlace>? Places { get; set; }

    [JsonPropertyName("widget_context_token")]
    public string? WidgetContextToken { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionPlace
{
    [JsonPropertyName("place_id")]
    public string? PlaceId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("review_snippets")]
    public List<InteractionReviewSnippet>? ReviewSnippets { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionReviewSnippet
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("review_id")]
    public string? ReviewId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

[JsonConverter(typeof(InteractionToolJsonConverter))]
public abstract class InteractionTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionFunctionTool : InteractionTool
{
    public InteractionFunctionTool()
    {
        Type = "function";
    }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public object? Parameters { get; set; }
}

public sealed class InteractionCodeExecutionTool : InteractionTool
{
    public InteractionCodeExecutionTool()
    {
        Type = "code_execution";
    }
}

public sealed class InteractionUrlContextTool : InteractionTool
{
    public InteractionUrlContextTool()
    {
        Type = "url_context";
    }
}

public sealed class InteractionComputerUseTool : InteractionTool
{
    public InteractionComputerUseTool()
    {
        Type = "computer_use";
    }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("excludedPredefinedFunctions")]
    public List<string>? ExcludedPredefinedFunctions { get; set; }
}

public sealed class InteractionMcpServerTool : InteractionTool
{
    public InteractionMcpServerTool()
    {
        Type = "mcp_server";
    }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("headers")]
    public object? Headers { get; set; }

    [JsonPropertyName("allowed_tools")]
    public List<InteractionAllowedTools>? AllowedTools { get; set; }
}

public sealed class InteractionGoogleSearchTool : InteractionTool
{
    public InteractionGoogleSearchTool()
    {
        Type = "google_search";
    }

    [JsonPropertyName("search_types")]
    public List<string>? SearchTypes { get; set; }
}

public sealed class InteractionFileSearchTool : InteractionTool
{
    public InteractionFileSearchTool()
    {
        Type = "file_search";
    }

    [JsonPropertyName("file_search_store_names")]
    public List<string>? FileSearchStoreNames { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("metadata_filter")]
    public string? MetadataFilter { get; set; }
}

public sealed class InteractionGoogleMapsTool : InteractionTool
{
    public InteractionGoogleMapsTool()
    {
        Type = "google_maps";
    }

    [JsonPropertyName("enable_widget")]
    public bool? EnableWidget { get; set; }

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }
}

public sealed class InteractionRetrievalTool : InteractionTool
{
    public InteractionRetrievalTool()
    {
        Type = "retrieval";
    }

    [JsonPropertyName("retrieval_types")]
    public List<string>? RetrievalTypes { get; set; }

    [JsonPropertyName("vertex_ai_search_config")]
    public InteractionVertexAiSearchConfig? VertexAiSearchConfig { get; set; }
}

public sealed class InteractionAllowedTools
{
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("tools")]
    public List<string>? Tools { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionVertexAiSearchConfig
{
    [JsonPropertyName("engine")]
    public string? Engine { get; set; }

    [JsonPropertyName("datastores")]
    public List<string>? Datastores { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionGenerationConfig
{
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("stop_sequences")]
    public List<string>? StopSequences { get; set; }

    [JsonPropertyName("thinking_level")]
    public string? ThinkingLevel { get; set; }

    [JsonPropertyName("thinking_summaries")]
    public string? ThinkingSummaries { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("speech_config")]
    public List<InteractionSpeechConfig>? SpeechConfig { get; set; }

    [JsonPropertyName("image_config")]
    public InteractionImageConfig? ImageConfig { get; set; }

    [JsonPropertyName("tool_choice")]
    public object? ToolChoice { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionSpeechConfig
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("speaker")]
    public string? Speaker { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionImageConfig
{
    [JsonPropertyName("aspect_ratio")]
    public string? AspectRatio { get; set; }

    [JsonPropertyName("image_size")]
    public string? ImageSize { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

[JsonConverter(typeof(InteractionAgentConfigJsonConverter))]
public abstract class InteractionAgentConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionDynamicAgentConfig : InteractionAgentConfig
{
    public InteractionDynamicAgentConfig()
    {
        Type = "dynamic";
    }
}

public sealed class InteractionDeepResearchAgentConfig : InteractionAgentConfig
{
    public InteractionDeepResearchAgentConfig()
    {
        Type = "deep-research";
    }

    [JsonPropertyName("thinking_summaries")]
    public string? ThinkingSummaries { get; set; }
}

public sealed class InteractionUsage
{
    [JsonPropertyName("total_input_tokens")]
    public int? TotalInputTokens { get; set; }

    [JsonPropertyName("input_tokens_by_modality")]
    public List<InteractionModalityTokens>? InputTokensByModality { get; set; }

    [JsonPropertyName("total_cached_tokens")]
    public int? TotalCachedTokens { get; set; }

    [JsonPropertyName("cached_tokens_by_modality")]
    public List<InteractionModalityTokens>? CachedTokensByModality { get; set; }

    [JsonPropertyName("total_output_tokens")]
    public int? TotalOutputTokens { get; set; }

    [JsonPropertyName("output_tokens_by_modality")]
    public List<InteractionModalityTokens>? OutputTokensByModality { get; set; }

    [JsonPropertyName("total_tool_use_tokens")]
    public int? TotalToolUseTokens { get; set; }

    [JsonPropertyName("tool_use_tokens_by_modality")]
    public List<InteractionModalityTokens>? ToolUseTokensByModality { get; set; }

    [JsonPropertyName("total_thought_tokens")]
    public int? TotalThoughtTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionModalityTokens
{
    [JsonPropertyName("modality")]
    public string? Modality { get; set; }

    [JsonPropertyName("tokens")]
    public int? Tokens { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

[JsonConverter(typeof(InteractionStreamEventJsonConverter))]
public abstract class InteractionStreamEventPart
{
    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = default!;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionStartEvent : InteractionStreamEventPart
{
    public InteractionStartEvent()
    {
        EventType = "interaction.start";
    }

    [JsonPropertyName("interaction")]
    public Interaction? Interaction { get; set; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }
}

public sealed class InteractionCompleteEvent : InteractionStreamEventPart
{
    public InteractionCompleteEvent()
    {
        EventType = "interaction.complete";
    }

    [JsonPropertyName("interaction")]
    public Interaction? Interaction { get; set; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }
}

public sealed class InteractionStatusUpdateEvent : InteractionStreamEventPart
{
    public InteractionStatusUpdateEvent()
    {
        EventType = "interaction.status_update";
    }

    [JsonPropertyName("interaction_id")]
    public string? InteractionId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }
}

public sealed class InteractionContentStartEvent : InteractionStreamEventPart
{
    public InteractionContentStartEvent()
    {
        EventType = "content.start";
    }

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("content")]
    public InteractionContent? Content { get; set; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }
}

public sealed class InteractionContentDeltaEvent : InteractionStreamEventPart
{
    public InteractionContentDeltaEvent()
    {
        EventType = "content.delta";
    }

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("delta")]
    public InteractionContentDeltaData? Delta { get; set; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }
}

public sealed class InteractionContentStopEvent : InteractionStreamEventPart
{
    public InteractionContentStopEvent()
    {
        EventType = "content.stop";
    }

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }
}

public sealed class InteractionErrorEvent : InteractionStreamEventPart
{
    public InteractionErrorEvent()
    {
        EventType = "error";
    }

    [JsonPropertyName("error")]
    public InteractionErrorInfo? Error { get; set; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }
}

public sealed class InteractionUnknownStreamEvent : InteractionStreamEventPart
{
}

public sealed class InteractionContentDeltaData
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionErrorInfo
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class InteractionsInputJsonConverter : JsonConverter<InteractionsInput>
{
    public override InteractionsInput? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        return root.ValueKind switch
        {
            JsonValueKind.String => new InteractionsInput(root.GetString() ?? string.Empty),
            JsonValueKind.Object => root.TryGetProperty("type", out _)
                ? new InteractionsInput(root.Deserialize<InteractionContent>(options) ?? new InteractionTextContent())
                : new InteractionsInput(root.Clone()),
            JsonValueKind.Array => ParseArray(root, options),
            JsonValueKind.Null or JsonValueKind.Undefined => new InteractionsInput(),
            _ => new InteractionsInput(root.Clone())
        };
    }

    public override void Write(Utf8JsonWriter writer, InteractionsInput value, JsonSerializerOptions options)
    {
        if (value.IsText)
        {
            writer.WriteStringValue(value.Text);
            return;
        }

        if (value.IsSingleContent)
        {
            JsonSerializer.Serialize(writer, value.SingleContent, options);
            return;
        }

        if (value.IsContent)
        {
            JsonSerializer.Serialize(writer, value.Content, options);
            return;
        }

        if (value.IsTurns)
        {
            JsonSerializer.Serialize(writer, value.Turns, options);
            return;
        }

        if (value.IsRaw)
        {
            value.Raw!.Value.WriteTo(writer);
            return;
        }

        writer.WriteNullValue();
    }

    private static InteractionsInput ParseArray(JsonElement root, JsonSerializerOptions options)
    {
        var isTurns = true;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("role", out _)
                || !item.TryGetProperty("content", out _))
            {
                isTurns = false;
                break;
            }
        }

        if (isTurns)
            return new InteractionsInput(root.Deserialize<List<InteractionTurn>>(options) ?? [], true);

        return new InteractionsInput(root.Deserialize<List<InteractionContent>>(options) ?? []);
    }
}

public sealed class InteractionTurnContentJsonConverter : JsonConverter<InteractionTurnContent>
{
    public override InteractionTurnContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        return root.ValueKind switch
        {
            JsonValueKind.String => new InteractionTurnContent(root.GetString() ?? string.Empty),
            JsonValueKind.Array => new InteractionTurnContent(root.Deserialize<List<InteractionContent>>(options) ?? []),
            JsonValueKind.Null or JsonValueKind.Undefined => new InteractionTurnContent(),
            _ => new InteractionTurnContent(root.Clone())
        };
    }

    public override void Write(Utf8JsonWriter writer, InteractionTurnContent value, JsonSerializerOptions options)
    {
        if (value.IsText)
        {
            writer.WriteStringValue(value.Text);
            return;
        }

        if (value.IsParts)
        {
            JsonSerializer.Serialize(writer, value.Parts, options);
            return;
        }

        if (value.IsRaw)
        {
            value.Raw!.Value.WriteTo(writer);
            return;
        }

        writer.WriteNullValue();
    }
}

public sealed class InteractionContentJsonConverter : JsonConverter<InteractionContent>
{
    private static readonly Dictionary<string, Type> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = typeof(InteractionTextContent),
        ["image"] = typeof(InteractionImageContent),
        ["audio"] = typeof(InteractionAudioContent),
        ["document"] = typeof(InteractionDocumentContent),
        ["video"] = typeof(InteractionVideoContent),
        ["thought"] = typeof(InteractionThoughtContent),
        ["function_call"] = typeof(InteractionFunctionCallContent),
        ["code_execution_call"] = typeof(InteractionCodeExecutionCallContent),
        ["url_context_call"] = typeof(InteractionUrlContextCallContent),
        ["mcp_server_tool_call"] = typeof(InteractionMcpServerToolCallContent),
        ["google_search_call"] = typeof(InteractionGoogleSearchCallContent),
        ["file_search_call"] = typeof(InteractionFileSearchCallContent),
        ["google_maps_call"] = typeof(InteractionGoogleMapsCallContent),
        ["function_result"] = typeof(InteractionFunctionResultContent),
        ["code_execution_result"] = typeof(InteractionCodeExecutionResultContent),
        ["url_context_result"] = typeof(InteractionUrlContextResultContent),
        ["google_search_result"] = typeof(InteractionGoogleSearchResultContent),
        ["mcp_server_tool_result"] = typeof(InteractionMcpServerToolResultContent),
        ["file_search_result"] = typeof(InteractionFileSearchResultContent),
        ["google_maps_result"] = typeof(InteractionGoogleMapsResultContent)
    };

    public override InteractionContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Interaction content must be an object.");

        var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(type) || !ContentTypes.TryGetValue(type, out var targetType))
            targetType = typeof(InteractionTextContent);

        return JsonSerializer.Deserialize(root.GetRawText(), targetType, options) as InteractionContent;
    }

    public override void Write(Utf8JsonWriter writer, InteractionContent value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}

public sealed class InteractionToolJsonConverter : JsonConverter<InteractionTool>
{
    private static readonly Dictionary<string, Type> ToolTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["function"] = typeof(InteractionFunctionTool),
        ["code_execution"] = typeof(InteractionCodeExecutionTool),
        ["url_context"] = typeof(InteractionUrlContextTool),
        ["computer_use"] = typeof(InteractionComputerUseTool),
        ["mcp_server"] = typeof(InteractionMcpServerTool),
        ["google_search"] = typeof(InteractionGoogleSearchTool),
        ["file_search"] = typeof(InteractionFileSearchTool),
        ["google_maps"] = typeof(InteractionGoogleMapsTool),
        ["retrieval"] = typeof(InteractionRetrievalTool)
    };

    public override InteractionTool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Interaction tool must be an object.");

        var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(type) || !ToolTypes.TryGetValue(type, out var targetType))
            targetType = typeof(InteractionFunctionTool);

        return JsonSerializer.Deserialize(root.GetRawText(), targetType, options) as InteractionTool;
    }

    public override void Write(Utf8JsonWriter writer, InteractionTool value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}

public sealed class InteractionAgentConfigJsonConverter : JsonConverter<InteractionAgentConfig>
{
    public override InteractionAgentConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Interaction agent_config must be an object.");

        var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
        var targetType = string.Equals(type, "deep-research", StringComparison.OrdinalIgnoreCase)
            ? typeof(InteractionDeepResearchAgentConfig)
            : typeof(InteractionDynamicAgentConfig);

        return JsonSerializer.Deserialize(root.GetRawText(), targetType, options) as InteractionAgentConfig;
    }

    public override void Write(Utf8JsonWriter writer, InteractionAgentConfig value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}

public sealed class InteractionStreamEventJsonConverter : JsonConverter<InteractionStreamEventPart>
{
    private static readonly Dictionary<string, Type> EventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["interaction.start"] = typeof(InteractionStartEvent),
        ["interaction.complete"] = typeof(InteractionCompleteEvent),
        ["interaction.status_update"] = typeof(InteractionStatusUpdateEvent),
        ["content.start"] = typeof(InteractionContentStartEvent),
        ["content.delta"] = typeof(InteractionContentDeltaEvent),
        ["content.stop"] = typeof(InteractionContentStopEvent),
        ["error"] = typeof(InteractionErrorEvent)
    };

    public override InteractionStreamEventPart? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Interaction stream event must be an object.");

        var eventType = root.TryGetProperty("event_type", out var typeProp) ? typeProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(eventType) || !EventTypes.TryGetValue(eventType, out var targetType))
            targetType = typeof(InteractionUnknownStreamEvent);

        return JsonSerializer.Deserialize(root.GetRawText(), targetType, options) as InteractionStreamEventPart;
    }

    public override void Write(Utf8JsonWriter writer, InteractionStreamEventPart value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}

public static class InteractionJson
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new InteractionsInputJsonConverter(),
            new InteractionTurnContentJsonConverter(),
            new InteractionContentJsonConverter(),
            new InteractionToolJsonConverter(),
            new InteractionAgentConfigJsonConverter(),
            new InteractionStreamEventJsonConverter()
        }
    };
}
