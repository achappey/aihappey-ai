using OpenAI.Responses;
using OAIC = OpenAI.Chat;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Text.Json;

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
                            BinaryData.FromBytes(Convert.FromBase64String(imageContentBlock.Data)),
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

    public static ResponseReasoningOptions? ToResponseCreationOptions(this JsonElement element)
    {
        if (!element.TryGetProperty(Constants.OpenAI, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind != JsonValueKind.Object)
            return null;

        var effort = reasoning.TryGetProperty("effort", out var effortProp) && effortProp.ValueKind == JsonValueKind.String
            ? effortProp.GetString()
            : null;

        var summary = reasoning.TryGetProperty("summary", out var summaryProp) && summaryProp.ValueKind == JsonValueKind.String
            ? summaryProp.GetString()
            : null;

        var options = new ResponseReasoningOptions
        {
        };

        if (summary != null)
        {
            options.ReasoningSummaryVerbosity = new ResponseReasoningSummaryVerbosity(summary);
        }

        if (effort != null)
        {
            options.ReasoningEffortLevel = new ResponseReasoningEffortLevel(effort);
        }

        return options;
    }

    public static ResponseTool? ToWebSearchTool(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(Constants.OpenAI, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("web_search", out var reasoning) || reasoning.ValueKind != JsonValueKind.Object)
            return null;

        var size = reasoning.TryGetProperty("search_context_size", out var effortProp) && effortProp.ValueKind == JsonValueKind.String
            ? effortProp.GetString()
            : null;

        return ResponseTool.CreateWebSearchTool(searchContextSize: size);
    }

    public static ResponseTool? ToFileSearchTool(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(Constants.OpenAI, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("file_search", out var reasoning) || reasoning.ValueKind != JsonValueKind.Object)
            return null;

        string[]? storeIds = [];

        if (reasoning.TryGetProperty("vector_store_ids", out var idsProp))
        {
            switch (idsProp.ValueKind)
            {
                case JsonValueKind.Array:
                    storeIds = [.. idsProp.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .Where(s => !string.IsNullOrWhiteSpace(s))];
                    break;

                // allow a single string as shorthand
                case JsonValueKind.String:
                    var one = idsProp.GetString();
                    if (!string.IsNullOrWhiteSpace(one))
                        storeIds = [one!];
                    break;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    storeIds = null;
                    break;
            }
        }

        if (storeIds == null || storeIds?.Length == 0)
        {
            return null;
        }

        var size = reasoning.TryGetProperty("max_num_results", out var effortProp) && effortProp.ValueKind == JsonValueKind.Number
            ? (int?)effortProp.GetInt32()
            : null;

        return ResponseTool.CreateFileSearchTool(storeIds, maxResultCount: size);
    }

    public static IEnumerable<ResponseTool>? ToMcpTools(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(Constants.OpenAI, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("mcp_list_tools", out var toolsProp) || toolsProp.ValueKind != JsonValueKind.Array)
            return null;

        var tools = new List<ResponseTool>();

        foreach (var toolEl in toolsProp.EnumerateArray())
        {
            if (toolEl.ValueKind == JsonValueKind.Object)
            {
                var dict = (Dictionary<string, object?>)JsonToObject(toolEl)!;

                // required: we need the 'type' string to create the tool
                if (dict.TryGetValue("type", out var type) && type is string typeStr && !string.IsNullOrWhiteSpace(typeStr))
                {
                    // normalize: allowed_tools can be array-of-strings or object; if it’s a string, wrap it
                    if (dict.TryGetValue("allowed_tools", out var allowedRaw) && allowedRaw is string oneStr)
                    {
                        dict["allowed_tools"] = new[] { oneStr };
                    }

                    tools.Add(typeStr.CreateCustomTool(dict));
                }
            }
            else if (toolEl.ValueKind == JsonValueKind.String)
            {
                // shorthand: just the type string
                var typeStr = toolEl.GetString();
                if (!string.IsNullOrWhiteSpace(typeStr))
                {
                    tools.Add(typeStr.CreateCustomTool(new Dictionary<string, object?>()));
                }
            }
        }

        return tools.Count > 0 ? tools : null;
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


    public static ResponseTool? ToCodeInterpreterTool(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(Constants.OpenAI, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty(Constants.CodeInterpreter, out var reasoning) || reasoning.ValueKind != JsonValueKind.Object)
            return null;

        if (reasoning.TryGetProperty("container", out var idsProp))
        {
            switch (idsProp.ValueKind)
            {
                case JsonValueKind.Object:
                    return Constants.CodeInterpreter.CreateCustomTool(new Dictionary<string, object?>() { { "container", new { type = "auto" } } });

                // allow a single string as shorthand
                case JsonValueKind.String:
                    var one = idsProp.GetString();
                    if (!string.IsNullOrWhiteSpace(one))
                        return Constants.CodeInterpreter.CreateCustomTool(new Dictionary<string, object?>() { { "container", one } });

                    break;

                default:
                    break;

            }
        }

        return Constants.CodeInterpreter.CreateCustomTool(new Dictionary<string, object?>() { { "container", new { type = "auto" } } });
    }

}