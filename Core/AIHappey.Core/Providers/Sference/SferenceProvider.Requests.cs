using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.Sference;

public sealed partial class SferenceProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static JsonObject EffectiveRequest(object request, Dictionary<string, object?>? metadata)
    {
        var body = JsonSerializer.SerializeToNode(request, Json)!.AsObject();
        if (metadata?.TryGetValue("sference", out var options) == true && options is not null)
        {
            var properties = JsonSerializer.SerializeToElement(options, Json);
            if (properties.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Sference provider options must be an object.");
            metadata["sference"] = properties;
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Name is "headers" or "tools")
                    continue;
                if (property.Name is "model" or "input" or "messages" or "stream")
                    throw new ArgumentException($"Sference provider options cannot override '{property.Name}'.");
                body[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
            if (properties.TryGetProperty("tools", out var tools))
                foreach (var tool in tools.EnumerateArray())
                    ValidateTool(tool);
        }
        return body;
    }

    private static void ValidateTool(JsonElement tool)
    {
        if (!tool.TryGetProperty("type", out var type) || type.GetString() != "function")
            throw Unsupported("server-side tools");
    }

    private static void ValidateCommon(JsonObject body, bool messages = false)
    {
        if (body["max_queue_wait_s"] is { } wait
            && (!double.TryParse(wait.ToJsonString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds is < 5 or > 300))
            throw new ArgumentException("Sference max_queue_wait_s must be between 5 and 300 seconds.");
        if (body["service_tier"] is { } tier
            && (messages || tier.GetValue<string>() is not ("flex" or "priority")))
            throw Unsupported("the requested service_tier on this API surface");
    }

    private static void PrepareChat(ChatCompletionOptions options, bool stream)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Model = NativeModel(options.Model);
        options.Stream = stream;
        var body = EffectiveRequest(options, options.GetProviderMetadata());
        ValidateCommon(body);
        if (body["background"]?.GetValue<bool>() == true)
            throw Unsupported("background Chat Completions; use Responses instead");
        if (body["metadata"] is JsonObject metadata
            && new[] { "stream_id", "completion_window", "completion_webhook", "webhook_token" }.Any(key => metadata[key] is not null))
            throw Unsupported("Streams, completion windows or webhooks on Chat Completions; use Responses instead");
        foreach (var tool in options.Tools ?? [])
            ValidateTool(JsonSerializer.SerializeToElement(tool, Json));
    }

    private static JsonElement? PrepareResponse(ResponseRequest options, bool stream)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Model = NativeModel(options.Model);
        options.Stream = stream;
        var body = EffectiveRequest(options, options.Metadata);
        ValidateCommon(body);
        if (body["previous_response_id"] is not null || body["conversation"] is not null)
            throw Unsupported("server-side conversation state; send the full conversation in input");
        if (body["context_management"] is not null || body["prompt_cache_options"] is not null)
            throw Unsupported("context_management or prompt_cache_options; prefix caching is automatic");
        if (body["metadata"] is JsonObject metadata)
        {
            if (metadata["completion_window"] is { } window
                && (window.GetValue<string>() != "24h" || body["background"]?.GetValue<bool>() != true))
                throw new ArgumentException("Sference completion_window must be '24h' and requires background: true.");
            if ((metadata["completion_webhook"] is not null || metadata["webhook_token"] is not null)
                && body["background"]?.GetValue<bool>() != true)
                throw new ArgumentException("Sference webhooks require background: true.");
        }
        foreach (var tool in options.Tools ?? [])
            if (tool.Type != "function")
                throw Unsupported("server-side tools");
        // Shared Responses transport strips gateway metadata. Restore native scheduling,
        // stream and webhook metadata without forwarding internal provider options.
        if (body["metadata"] is not JsonObject nativeMetadata)
            return null;
        nativeMetadata.Remove("sference");
        nativeMetadata.Remove("providerMetadata");
        return nativeMetadata.Count == 0 ? null
            : JsonSerializer.SerializeToElement(new JsonObject { ["metadata"] = nativeMetadata.DeepClone() }, Json);
    }

    private static void PrepareMessages(MessagesRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Model = NativeModel(request.Model);
        request.Stream = stream;
        if (request.MaxTokens <= 0)
            throw new ArgumentException("Sference Messages requires a positive max_tokens.");
        ValidateCommon(JsonSerializer.SerializeToNode(request, Json)!.AsObject(), messages: true);
        if (request.OutputConfig is not null || request.ContextManagement is not null || request.Compaction is not null
            || request.Container is not null || request.InferenceGeo is not null)
            throw Unsupported("output_config, context management, containers or inference geography on Messages");
        if (request.Thinking is { } thinking
            && (thinking.Type is not ("enabled" or "disabled") || thinking.BudgetTokens is not null || thinking.Display is not null))
            throw Unsupported("adaptive thinking, thinking budgets or thinking display; use max_tokens to bound output");
        if (request.AdditionalProperties?.TryGetValue("background", out var background) == true && background.ValueKind != JsonValueKind.False)
            throw Unsupported("background Messages");
        foreach (var message in request.Messages)
        {
            if (message.Role is not ("user" or "assistant"))
                throw new ArgumentException("Sference Messages supports only user and assistant roles; use system for instructions.");
            ValidateContent(message.Content);
        }
        foreach (var tool in request.Tools ?? [])
        {
            if (tool.Type is not (null or "function"))
                throw Unsupported("server-side Messages tools");
            // Shared Messages transport deduplicates by Name, including OpenAI-shaped tools.
            if (tool.Type == "function" && tool.AdditionalProperties?.TryGetValue("function", out var function) == true)
                tool.Name = ReadString(function, "name")
                    ?? throw new ArgumentException("Sference function tools require a function name.");
        }
        if (request.ToolChoice?.DisableParallelToolUse == true)
            throw Unsupported("disable_parallel_tool_use on Messages");

        // Sference does not reliably honor Anthropic's any/tool forcing forms.
        if (request.ToolChoice is { Type: "any" } any)
            any.Type = "required";
        else if (request.ToolChoice is { Type: "tool" } forced)
        {
            if (forced.AdditionalProperties?.TryGetValue("name", out var name) != true
                || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                throw new ArgumentException("Forced Sference tool choice requires a tool name.");
            request.ToolChoice = new MessageToolChoice
            {
                Type = "function",
                AdditionalProperties = new()
                {
                    ["function"] = JsonSerializer.SerializeToElement(new { name = name.GetString() })
                }
            };
        }
    }

    private static void ValidateContent(MessagesContent content)
    {
        if (content.Raw is { } raw)
        {
            if (raw.ValueKind == JsonValueKind.String) return;
            if (raw.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Sference message content must be text or a block array.");
            foreach (var block in raw.EnumerateArray())
                ValidateContentBlock(block);
        }
        foreach (var block in content.Blocks ?? [])
            ValidateContentBlock(JsonSerializer.SerializeToElement(block, Json));
    }

    private static void ValidateContentBlock(JsonElement block)
    {
        var type = block.GetProperty("type").GetString();
        if (type is not ("text" or "image" or "thinking" or "tool_use" or "tool_result"))
            throw Unsupported($"Messages content block '{type}'");
        if (type == "tool_result" && block.TryGetProperty("content", out var nested) && nested.ValueKind == JsonValueKind.Array)
            foreach (var child in nested.EnumerateArray())
                ValidateContentBlock(child);
    }
}
