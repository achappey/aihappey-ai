using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Abstractions.Http;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Mistral;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Mistral;

public sealed record MistralParsedConversationEvent(string Type, JsonNode Payload)
{
    public string? GetString(string propertyName)
        => MistralExtensions.GetString(Payload, propertyName);

    public JsonNode? GetNode(string propertyName)
        => Payload[propertyName];
}

public sealed record MistralParsedContentPart(
    string Type,
    string? Text = null,
    string? FileId = null,
    string? FileName = null,
    string? FileType = null,
    string? Url = null,
    string? Title = null,
    JsonNode? Raw = null);

public static partial class MistralExtensions
{
    private static readonly JsonSerializerOptions UnifiedJson = JsonSerializerOptions.Web;
    private static readonly string[] RawMistralNodeMetadataKeys = ["mistral.raw", "mistral.content.raw", "mistral.content", "mistral.node"];

    public static ProviderBackendCaptureRequest? GetMistralBackendCapture(this AIRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        try
        {
            return request.Metadata?.GetProviderOption<ProviderBackendCaptureRequest>(providerId, "capture")
                ?? request.Metadata?.GetProviderOption<ProviderBackendCaptureRequest>(providerId, "backend_capture");
        }
        catch
        {
            return null;
        }
    }

    public static MistralProviderMetadata? GetMistralProviderMetadata(this AIRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        try
        {
            return request.Metadata.GetProviderMetadata<MistralProviderMetadata>(providerId);
        }
        catch
        {
            return null;
        }
    }

    public static string? BuildMistralUnifiedInstructions(this AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sections = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            sections.Add(request.Instructions.Trim());

        foreach (var item in request.Input?.Items ?? [])
        {
            if (!string.Equals(NormalizeUnifiedRole(item.Role), "system", StringComparison.Ordinal))
                continue;

            var text = FlattenSystemContent(item.Content);
            if (!string.IsNullOrWhiteSpace(text))
                sections.Add(text);
        }

        return sections.Count == 0 ? null : string.Join("\n\n", sections);
    }

    public static List<object> BuildMistralUnifiedConversationInputs(this AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var inputs = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
        {
            inputs.Add(new Dictionary<string, object?>
            {
                ["type"] = "message.input",
                ["role"] = "user",
                ["content"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = request.Input.Text
                    }
                }
            });
        }

        foreach (var item in request.Input?.Items ?? [])
            inputs.AddRange(item.BuildMistralUnifiedConversationEntries());

        return inputs;
    }

    public static IEnumerable<object> BuildMistralUnifiedConversationEntries(this AIInputItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var role = NormalizeUnifiedRole(item.Role);
        if (string.Equals(role, "system", StringComparison.Ordinal))
            yield break;

        var contentParts = new List<object>();

        foreach (var part in item.Content ?? [])
        {
            if (part is AIToolCallContentPart)
                continue;

            var mapped = part.ToMistralUnifiedContentPart();
            if (mapped is not null)
                contentParts.Add(mapped);
        }

        if (contentParts.Count > 0)
        {
            yield return new Dictionary<string, object?>
            {
                ["type"] = "message.input",
                ["role"] = role,
                ["content"] = contentParts
            };
        }

        foreach (var toolPart in item.Content?.OfType<AIToolCallContentPart>() ?? Enumerable.Empty<AIToolCallContentPart>())
        {
            if (toolPart.ProviderExecuted == true || string.IsNullOrWhiteSpace(toolPart.ToolCallId))
                continue;

            yield return new Dictionary<string, object?>
            {
                ["type"] = "function.call",
                ["tool_call_id"] = toolPart.ToolCallId,
                ["name"] = toolPart.ToolName ?? "tool",
                ["arguments"] = JsonSerializer.Serialize(toolPart.Input ?? new { }, UnifiedJson)
            };

            if (toolPart.Output is not null)
            {
                yield return new Dictionary<string, object?>
                {
                    ["type"] = "function.result",
                    ["tool_call_id"] = toolPart.ToolCallId,
                    ["result"] = SerializeUnifiedToolOutput(toolPart.Output)
                };
            }
        }
    }

    public static object? ToMistralUnifiedContentPart(this AIContentPart part)
    {
        ArgumentNullException.ThrowIfNull(part);

        if (TryExtractRawMistralNode(part.Metadata) is { } rawNode)
            return rawNode;

        return part switch
        {
            AITextContentPart textPart when !string.IsNullOrWhiteSpace(textPart.Text) => new
            {
                type = "text",
                text = textPart.Text
            },
            AIReasoningContentPart reasoningPart when !string.IsNullOrWhiteSpace(reasoningPart.Text) => new
            {
                type = "text",
                text = reasoningPart.Text
            },
            AIFileContentPart filePart => filePart.ToMistralUnifiedFileContentPart(),
            _ => null
        };
    }

    public static object ToMistralUnifiedFileContentPart(this AIFileContentPart filePart)
    {
        ArgumentNullException.ThrowIfNull(filePart);

        var imageUrl = TryNormalizeImageInput(filePart);
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            return new
            {
                type = "image_url",
                image_url = imageUrl
            };
        }

        throw new NotSupportedException(
            "Mistral unified conversations only supports image file inputs unless metadata already contains a native Mistral content block.");
    }

    public static List<JsonNode> BuildMistralConversationTools(this AIRequest request, MistralProviderMetadata? providerMetadata)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tools = new List<JsonNode>();

        foreach (var tool in request.Tools ?? Enumerable.Empty<AIToolDefinition>())
        {
            if (TryExtractRawMistralToolNode(tool.Metadata) is { } rawNode)
            {
                tools.Add(rawNode);
                continue;
            }

            AddSerializedToolNode(tools, new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.InputSchema
                }
            });
        }

        tools.AddRange(ResolveProviderConversationTools(providerMetadata));
        return tools;
    }

    public static string NormalizeUnifiedRole(string? role)
        => string.IsNullOrWhiteSpace(role)
            ? "user"
            : role.Trim().ToLowerInvariant() switch
            {
                "tool" => "assistant",
                _ => role.Trim().ToLowerInvariant()
            };

    public static string? NormalizeUnifiedToolChoice(object? toolChoice)
    {
        if (toolChoice is null)
            return null;

        if (toolChoice is string text)
            return string.IsNullOrWhiteSpace(text) ? null : text;

        if (toolChoice is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.String)
                return json.GetString();

            if (json.ValueKind == JsonValueKind.Object
                && json.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String)
            {
                return typeElement.GetString();
            }
        }

        if (toolChoice is JsonNode node)
        {
            var type = node["type"];
            if (type is JsonValue value && value.TryGetValue<string>(out var typeText))
                return typeText;
        }

        try
        {
            var element = JsonSerializer.SerializeToElement(toolChoice, UnifiedJson);
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String)
            {
                return typeElement.GetString();
            }
        }
        catch
        {
        }

        return toolChoice.ToString();
    }

    public static string FlattenSystemContent(List<AIContentPart>? content)
    {
        if (content is null || content.Count == 0)
            return string.Empty;

        var textParts = new List<string>();

        foreach (var part in content)
        {
            switch (part)
            {
                case AITextContentPart text when !string.IsNullOrWhiteSpace(text.Text):
                    textParts.Add(text.Text.Trim());
                    break;

                case AIReasoningContentPart reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                    textParts.Add(reasoning.Text.Trim());
                    break;

                case AIFileContentPart:
                    throw new NotSupportedException("Mistral unified conversations does not support file content in system messages.");

                case AIToolCallContentPart:
                    throw new NotSupportedException("Mistral unified conversations does not support tool calls inside system messages.");
            }
        }

        return string.Join("\n\n", textParts);
    }

    public static string SerializeUnifiedToolOutput(object output)
        => output is string text
            ? text
            : JsonSerializer.Serialize(output, UnifiedJson);

    public static JsonNode? TryExtractRawMistralToolNode(Dictionary<string, object?>? metadata)
        => TryExtractRawMistralNode(metadata);

    public static JsonNode? TryExtractRawMistralNode(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        foreach (var key in RawMistralNodeMetadataKeys)
        {
            if (!metadata.TryGetValue(key, out var value) || value is null)
                continue;

            if (TryConvertToJsonNode(value) is { } node)
                return node;
        }

        return null;
    }

    public static JsonNode? TryConvertToJsonNode(object value)
    {
        try
        {
            return value switch
            {
                JsonNode node => node.DeepClone(),
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                string text when !string.IsNullOrWhiteSpace(text)
                    && (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')) => JsonNode.Parse(text),
                _ => JsonSerializer.SerializeToNode(value, UnifiedJson)
            };
        }
        catch
        {
            return null;
        }
    }

    public static string? TryNormalizeImageInput(AIFileContentPart filePart)
    {
        ArgumentNullException.ThrowIfNull(filePart);

        var mediaType = ResolveImageMediaType(filePart);
        if (mediaType is null)
            return null;

        return filePart.Data switch
        {
            string text when text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) => text,
            string text when text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                              || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) => text,
            string text when LooksLikeBase64(text) => $"data:{mediaType};base64,{text}",
            byte[] bytes => ToDataUrl(bytes, mediaType),
            JsonElement element when element.ValueKind == JsonValueKind.String => NormalizeImageString(element.GetString(), mediaType),
            _ => null
        };

        static string? NormalizeImageString(string? text, string mediaType)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            return LooksLikeBase64(text) ? $"data:{mediaType};base64,{text}" : null;
        }
    }

    public static string? ResolveImageMediaType(AIFileContentPart filePart)
    {
        ArgumentNullException.ThrowIfNull(filePart);

        if (!string.IsNullOrWhiteSpace(filePart.MediaType)
            && filePart.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return filePart.MediaType;
        }

        var guessedFromFilename = GuessImageMediaType(filePart.Filename);
        if (!string.IsNullOrWhiteSpace(guessedFromFilename))
            return guessedFromFilename;

        if (filePart.Data is string text
            && text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var separatorIndex = text.IndexOf(';');
            if (separatorIndex > 5)
                return text.Substring(5, separatorIndex - 5);
        }

        return null;
    }

    public static string? GuessImageMediaType(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return null;

        return Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            _ => null
        };
    }

    public static bool LooksLikeBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length < 32 || trimmed.Length % 4 != 0)
            return false;

        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || ch is '+' or '/' or '=')
                continue;

            return false;
        }

        return true;
    }

    public static string ToDataUrl(byte[] bytes, string? mediaType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return $"data:{mediaType ?? MediaTypeNames.Application.Octet};base64,{Convert.ToBase64String(bytes)}";
    }

    public static JsonNode? ToToolArrayNode(IEnumerable<JsonNode> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var array = new JsonArray();
        foreach (var tool in tools)
            array.Add(tool.DeepClone());

        return array.Count == 0 ? null : array;
    }

    public static void AddSerializedToolNode(List<JsonNode> tools, object? tool)
    {
        ArgumentNullException.ThrowIfNull(tools);

        if (tool is null)
            return;

        var node = JsonSerializer.SerializeToNode(tool, UnifiedJson);
        if (node is not null)
            tools.Add(node);
    }

    public static JsonNode? TryCreateToolNode(JsonElement tool)
    {
        if (tool.ValueKind != JsonValueKind.Object)
            return null;

        if (!tool.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(typeElement.GetString()))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(tool.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static object DeserializeToolInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new { };

        try
        {
            return JsonSerializer.Deserialize<object>(input, UnifiedJson) ?? new { };
        }
        catch (JsonException)
        {
            return input;
        }
    }

    public static AIToolCallContentPart CreateUnifiedToolCallContentPart(JsonNode output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var toolName = GetString(output, "name") ?? string.Empty;

        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = GetString(output, "tool_call_id") ?? GetString(output, "id") ?? Guid.NewGuid().ToString("n"),
            ToolName = toolName,
            Title = toolName,
            Input = DeserializeToolInput(ReadNodeAsString(output["arguments"])),
            Output = ToUntypedObject(output["result"]),
            State = GetString(output, "status") ?? GetString(output, "type"),
            ProviderExecuted = IsProviderExecutedTool(toolName),
            Metadata = new Dictionary<string, object?>
            {
                ["mistral.raw"] = output.DeepClone()
            }
        };
    }

    public static bool LooksLikeToolCallOutput(JsonNode output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var type = GetString(output, "type") ?? string.Empty;
        return type.Contains("function", StringComparison.OrdinalIgnoreCase)
               || type.Contains("tool", StringComparison.OrdinalIgnoreCase)
               || output["tool_call_id"] is not null
               || output["arguments"] is not null;
    }

    public static object? ToUntypedObject(JsonNode? node)
    {
        if (node is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<object>(node.ToJsonString(UnifiedJson), UnifiedJson);
        }
        catch
        {
            return node.ToJsonString(UnifiedJson);
        }
    }

    public static bool IsProviderExecutedTool(string? toolName)
        => toolName is "code_interpreter" or "image_generation" or "web_search" or "web_search_premium";

    public static List<JsonNode> ResolveProviderConversationTools(MistralProviderMetadata? metadata)
    {
        if (metadata?.Tools is null)
            return [];

        var passthroughTools = new List<JsonNode>(metadata.Tools.Length);

        foreach (var tool in metadata.Tools)
        {
            if (TryCreateToolNode(tool) is { } node)
                passthroughTools.Add(node);
        }

        return passthroughTools;
    }

    public static MistralParsedConversationEvent ParseConversationStreamEventEnvelope(string? sseEvent, string data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);

        var payload = JsonNode.Parse(data) ?? new JsonObject();
        var type = sseEvent ?? GetString(payload, "type") ?? string.Empty;
        return new MistralParsedConversationEvent(type, payload);
    }

    public static IEnumerable<MistralParsedContentPart> EnumerateConversationContentParts(JsonNode? content)
    {
        if (content is null)
            yield break;

        if (content is JsonValue value && value.TryGetValue<string>(out var textValue))
        {
            if (!string.IsNullOrEmpty(textValue))
                yield return new MistralParsedContentPart("text", Text: textValue, Raw: content);

            yield break;
        }

        if (content is JsonArray array)
        {
            foreach (var item in array)
            {
                var parsed = ParseConversationContentPart(item);
                if (parsed is not null)
                    yield return parsed;
            }

            yield break;
        }

        var single = ParseConversationContentPart(content);
        if (single is not null)
            yield return single;
    }

    public static string ReadNodeAsString(JsonNode? node)
    {
        if (node is null)
            return string.Empty;

        if (node is JsonValue value && value.TryGetValue<string>(out var textValue))
            return textValue ?? string.Empty;

        return node.ToJsonString(UnifiedJson);
    }

    public static string? GetString(JsonNode? node, string propertyName)
    {
        var valueNode = node?[propertyName];
        if (valueNode is null)
            return null;

        if (valueNode is JsonValue value && value.TryGetValue<string>(out var textValue))
            return textValue;

        return valueNode.ToJsonString(UnifiedJson);
    }

    public static int? GetInt32(JsonNode? node, string propertyName)
    {
        var valueNode = node?[propertyName];
        if (valueNode is not JsonValue value)
            return null;

        if (value.TryGetValue<int>(out var intValue))
            return intValue;

        if (value.TryGetValue<long>(out var longValue))
            return (int)longValue;

        if (value.TryGetValue<double>(out var doubleValue))
            return (int)doubleValue;

        return null;
    }

    public static bool? GetBoolean(JsonNode? node, string propertyName)
    {
        var valueNode = node?[propertyName];
        if (valueNode is not JsonValue value)
            return null;

        return value.TryGetValue<bool>(out var boolValue) ? boolValue : null;
    }

    private static MistralParsedContentPart? ParseConversationContentPart(JsonNode? node)
    {
        if (node is null)
            return null;

        if (node is JsonValue value && value.TryGetValue<string>(out var textValue))
            return string.IsNullOrEmpty(textValue)
                ? null
                : new MistralParsedContentPart("text", Text: textValue, Raw: node);

        var type = GetString(node, "type") ?? string.Empty;

        return type switch
        {
            "output_text" or "text" => new MistralParsedContentPart(
                type,
                Text: GetString(node, "text") ?? GetString(node, "content"),
                Raw: node),
            "tool_file" => new MistralParsedContentPart(
                type,
                FileId: GetString(node, "file_id"),
                FileName: GetString(node, "file_name"),
                FileType: GetString(node, "file_type"),
                Raw: node),
            "tool_reference" => new MistralParsedContentPart(
                type,
                Url: GetString(node, "url"),
                Title: GetString(node, "title"),
                Raw: node),
            "document_url" => new MistralParsedContentPart(
                type,
                Url: GetString(node, "document_url"),
                Title: GetString(node, "document_name"),
                Raw: node),
            _ => new MistralParsedContentPart(type, Raw: node)
        };
    }
}
