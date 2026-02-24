using OpenAI.Responses;
using OAIC = OpenAI.Chat;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.OpenAI;

public static class SamplingMessageExtensions
{
    public static ResponseContentPart? ToResponseContentPart(this ContentBlock contentBlock)
    {
        if (contentBlock is TextContentBlock textContentBlock)
        {
            return ResponseContentPart.CreateInputTextPart(textContentBlock.Text);
        }

        if (contentBlock is ImageContentBlock imageContentBlock)
        {
            return ResponseContentPart.CreateInputImagePart(
                            BinaryData.FromBytes(imageContentBlock.Data),
                            imageContentBlock.MimeType,
                            ResponseImageDetailLevel.High
                        );
        }

        return null;
    }

    public static IEnumerable<ResponseContentPart?> ToToResponseContentParts(this SamplingMessage samplingMessage) =>
        samplingMessage.Content.Select(a => a.ToResponseContentPart());

    public static ResponseItem ToResponseItem(this SamplingMessage samplingMessage)
        => samplingMessage.Role == Role.User ?
            ResponseItem.CreateUserMessageItem(samplingMessage.ToToResponseContentParts().OfType<ResponseContentPart>())
            : ResponseItem.CreateAssistantMessageItem(samplingMessage.ToText());

    public static OAIC.ChatMessage ToChatMessage(this SamplingMessage samplingMessage)
            => samplingMessage.Role == Role.User ?
                OAIC.ChatMessage.CreateUserMessage(samplingMessage.ToText())
                : OAIC.ChatMessage.CreateAssistantMessage(samplingMessage.ToText());

    public static IEnumerable<ResponseItem> ToResponseItems(this IList<SamplingMessage> samplingMessages)
        => samplingMessages.Select(a => a.ToResponseItem());

    public static IEnumerable<OAIC.ChatMessage> ToChatMessages(this IList<SamplingMessage> samplingMessages)
        => samplingMessages.Select(a => a.ToChatMessage());

    public static CreateResponseOptions ToResponseCreationOptions(this CreateMessageRequestParams chatRequest) => new()
    {
        TruncationMode = ResponseTruncationMode.Auto,
        StoredOutputEnabled = false,
        Instructions = chatRequest.SystemPrompt,
        MaxOutputTokenCount = chatRequest.MaxTokens,
        Temperature = chatRequest.Temperature,
        ReasoningOptions = chatRequest.Metadata?.ToResponseCreationOptions()
    };

    public static ResponseReasoningOptions? ToResponseCreationOptions(this JsonObject? obj)
    {
        var reasoning = GetOpenAITool(obj, "reasoning");
        if (reasoning is null)
            return null;

        var effort =
            reasoning["effort"] is JsonValue e &&
            e.TryGetValue<string>(out var effortStr)
                ? effortStr
                : null;

        var summary =
            reasoning["summary"] is JsonValue s &&
            s.TryGetValue<string>(out var summaryStr)
                ? summaryStr
                : null;

        var options = new ResponseReasoningOptions();

        if (summary is not null)
            options.ReasoningSummaryVerbosity = new ResponseReasoningSummaryVerbosity(summary);

        if (effort is not null)
            options.ReasoningEffortLevel = new ResponseReasoningEffortLevel(effort);

        return options;
    }

    public static ResponseTool? ToWebSearchTool(this JsonObject? obj)
    {
        var webSearch = GetOpenAITool(obj, "web_search");
        if (webSearch is null)
            return null;

        var size =
            webSearch["search_context_size"] is JsonValue v &&
            v.TryGetValue<string>(out var sizeStr)
                ? sizeStr
                : null;

        return ResponseTool.CreateWebSearchTool(searchContextSize: size);
    }
    public static ResponseTool? ToFileSearchTool(this JsonObject? obj)
    {
        var fileSearch = GetOpenAITool(obj, "file_search");
        if (fileSearch is null)
            return null;

        string[]? storeIds = null;

        var idsNode = fileSearch["vector_store_ids"];

        if (idsNode is JsonArray arr)
        {
            storeIds = [.. arr
                .OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .OfType<string>()];
        }
        else if (idsNode is JsonValue single &&
                 single.TryGetValue<string>(out var one) &&
                 !string.IsNullOrWhiteSpace(one))
        {
            storeIds = [one];
        }

        if (storeIds is null || storeIds.Length == 0)
            return null;

        int? maxResults =
            fileSearch["max_num_results"] is JsonValue n &&
            n.TryGetValue<int>(out var parsed)
                ? parsed
                : null;

        return ResponseTool.CreateFileSearchTool(storeIds, maxResultCount: maxResults);
    }
    public static IEnumerable<ResponseTool>? ToMcpTools(this JsonObject? obj)
    {
        if (obj?[Constants.OpenAI] is not JsonObject openai)
            return null;

        if (openai["mcp_list_tools"] is not JsonArray toolsArray)
            return null;

        var tools = new List<ResponseTool>();

        foreach (var node in toolsArray)
        {
            if (node is JsonObject toolObj)
            {
                var element = JsonSerializer.SerializeToElement(toolObj);
                var dict = (Dictionary<string, object?>)JsonToObject(element)!;

                if (dict.TryGetValue("type", out var type) &&
                    type is string typeStr &&
                    !string.IsNullOrWhiteSpace(typeStr))
                {
                    if (dict.TryGetValue("allowed_tools", out var allowed) &&
                        allowed is string oneStr)
                    {
                        dict["allowed_tools"] = new[] { oneStr };
                    }

                    tools.Add(typeStr.CreateCustomTool(dict));
                }
            }
            else if (node is JsonValue v &&
                     v.TryGetValue<string>(out var typeStr) &&
                     !string.IsNullOrWhiteSpace(typeStr))
            {
                tools.Add(typeStr.CreateCustomTool(new Dictionary<string, object?>()));
            }
        }

        return tools.Count > 0 ? tools : null;
    }
    private static JsonObject? GetOpenAITool(JsonObject? root, string key)
    {
        if (root?[Constants.OpenAI] is not JsonObject openai)
            return null;

        return openai[key] as JsonObject;
    }

    public static string ToStopReason(this OAIC.ChatFinishReason finishReason)
       => finishReason switch
       {
           OAIC.ChatFinishReason.Stop => "endTurn",
           OAIC.ChatFinishReason.Length => "maxTokens",
           OAIC.ChatFinishReason.ToolCalls => "toolUse",
           _ => finishReason.ToString()
       };


    /// <summary>
    /// Recursively converts a JsonElement into native CLR objects:
    /// - string, bool, numbers (int/long/double), null
    /// - arrays -> object?[]
    /// - objects -> Dictionary&lt;string, object?&gt;
    /// </summary>
    private static object? JsonToObject(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return el.GetString();

            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l)) return l;
                if (el.TryGetDouble(out var d)) return d;
                return el.GetRawText(); // fallback

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;

            case JsonValueKind.Array:
                {
                    var list = new List<object?>();
                    foreach (var item in el.EnumerateArray())
                        list.Add(JsonToObject(item));
                    return list.ToArray();
                }

            case JsonValueKind.Object:
                {
                    var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var prop in el.EnumerateObject())
                        dict[prop.Name] = JsonToObject(prop.Value);
                    return dict;
                }

            default:
                return el.GetRawText();
        }
    }

    public static ResponseTool? ToCodeInterpreterTool(this JsonObject? obj)
    {
        if (obj?[Constants.OpenAI] is not JsonObject openai ||
            openai[Constants.CodeInterpreter] is not JsonObject reasoning)
            return null;

        object container = reasoning["container"] switch
        {
            JsonObject => new { type = "auto" },
            JsonValue v when v.TryGetValue<string>(out var s) &&
                             !string.IsNullOrWhiteSpace(s) => s,
            _ => new { type = "auto" }
        };

        return Constants.CodeInterpreter.CreateCustomTool(
            new Dictionary<string, object?> { { "container", container } });
    }
}