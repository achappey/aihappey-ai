using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.ChatCompletions.Mapping;

public static partial class ChatCompletionsUnifiedMapper
{
    public static AIRequest ToUnifiedRequest(this ChatCompletionOptions request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var raw = JsonSerializer.SerializeToElement(request, Json);

        return raw.ToUnifiedRequest(providerId);
    }

    public static AIRequest ToUnifiedRequest(this JsonElement request, string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (request.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Chat completions request JSON must be an object.", nameof(request));

        var model = request.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString()
            : null;

        var messages = request.TryGetProperty("messages", out var msgEl) && msgEl.ValueKind == JsonValueKind.Array
            ? ParseRequestMessages(msgEl).ToList()
            : [];

        var tools = request.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array
            ? ParseTools(toolsEl).ToList()
            : [];

        var metadata = BuildUnifiedRequestMetadata(request);

        return new AIRequest
        {
            ProviderId = providerId,
            Model = model,
            Input = new AIInput
            {
                Items = messages,
                Metadata = new Dictionary<string, object?>
                {
                    ["chatcompletions.input.raw_messages"] = msgEl.ValueKind == JsonValueKind.Array ? msgEl.Clone() : null
                }
            },
            Temperature = ExtractValue<float?>(request, "temperature"),
            TopP = ExtractValue<double?>(request, "top_p"),
            MaxOutputTokens = ExtractValue<int?>(request, "max_completion_tokens") ?? ExtractValue<int?>(request, "max_tokens"),
            Stream = ExtractValue<bool?>(request, "stream"),
            ParallelToolCalls = ExtractValue<bool?>(request, "parallel_tool_calls"),
            ToolChoice = request.TryGetProperty("tool_choice", out var toolChoiceEl) ? toolChoiceEl.Clone() : null,
            ResponseFormat = request.TryGetProperty("response_format", out var responseFormatEl) ? responseFormatEl.Clone() : null,
            Tools = tools,
            Metadata = metadata
        };
    }

    public static ChatCompletionOptions ToChatCompletionOptions(this AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedToolChoice = NormalizeToolChoice(request.ToolChoice, request.Tools);

        var options = new ChatCompletionOptions
        {
            Model = request.Model ?? string.Empty,
            Temperature = request.Temperature,
            ParallelToolCalls = request.ParallelToolCalls,
            Stream = request.Stream,
            Messages = ToChatMessages(request.Input).ToList(),
            Tools = ToChatTools(request.Tools).ToList(),
            ToolChoice = normalizedToolChoice,
            ResponseFormat = request.ResponseFormat,
            Metadata = request.Metadata,
            Store = ExtractMetadataValue<bool?>(request.Metadata, "chatcompletions.request.store")
        };

        return options;
    }

    public static JsonElement ToChatCompletionRequestJson(this AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = new JsonObject();

        var raw = ExtractMetadataElement(request.Metadata, "chatcompletions.request.raw");
        if (raw is { ValueKind: JsonValueKind.Object })
            root = JsonNode.Parse(raw.Value.GetRawText())?.AsObject() ?? new JsonObject();

        var unmapped = ExtractMetadataElement(request.Metadata, "chatcompletions.request.unmapped");
        if (unmapped is { ValueKind: JsonValueKind.Object })
        {
            foreach (var prop in unmapped.Value.EnumerateObject())
                root[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
            root["model"] = request.Model;

        Set(root, "temperature", request.Temperature);
        Set(root, "top_p", request.TopP);
        Set(root, "max_completion_tokens", request.MaxOutputTokens);
        Set(root, "stream", request.Stream);
        Set(root, "parallel_tool_calls", request.ParallelToolCalls);

        if (request.ToolChoice is not null)
            root["tool_choice"] = ToJsonNode(request.ToolChoice);

        if (request.ResponseFormat is not null)
            root["response_format"] = ToJsonNode(request.ResponseFormat);

        if (request.Tools is { Count: > 0 })
        {
            root["tools"] = JsonValue.Create(JsonSerializer.Serialize(request.Tools.Select(ToRawChatTool).ToList(), Json));
            root["tools"] = ToJsonNode(request.Tools.Select(ToRawChatTool).ToList());
        }

        var messages = ToChatMessages(request.Input).ToList();
        if (messages.Count > 0)
            root["messages"] = ToJsonNode(messages);

        var store = ExtractMetadataValue<bool?>(request.Metadata, "chatcompletions.request.store");
        if (store is not null)
            root["store"] = store.Value;

        return JsonSerializer.SerializeToElement(root, Json);
    }

    private static Dictionary<string, object?> BuildUnifiedRequestMetadata(JsonElement request)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["chatcompletions.request.raw"] = request.Clone()
        };

        foreach (var prop in request.EnumerateObject())
            metadata[$"chatcompletions.request.{prop.Name}"] = prop.Value.Clone();

        var unmapped = new Dictionary<string, JsonElement>();
        foreach (var prop in request.EnumerateObject())
        {
            if (!MappedRequestFields.Contains(prop.Name))
                unmapped[prop.Name] = prop.Value.Clone();
        }

        metadata["chatcompletions.request.unmapped"] = JsonSerializer.SerializeToElement(unmapped, Json);
        return metadata;
    }
}
