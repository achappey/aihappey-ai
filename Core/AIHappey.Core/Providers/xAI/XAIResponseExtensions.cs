using System.Text.Json;
using AIHappey.Common.Model.Providers.XAI;

namespace AIHappey.Core.Providers.xAI;

public static partial class XAIResponseExtensions
{   
    public static XAIReasoning? ToReasoning(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(XAIRequestExtensions.XAIIdentifier, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("reasoning", out var webSearch) || webSearch.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<XAIReasoning>(webSearch.GetRawText());
    }

    public static XAIXCodeExecution? ToCodeExecution(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(XAIRequestExtensions.XAIIdentifier, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("code_execution", out var webSearch) || webSearch.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<XAIXCodeExecution>(webSearch.GetRawText());
    }

    public static XAIXSearch? ToXSearchTool(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(XAIRequestExtensions.XAIIdentifier, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("x_search", out var webSearch) || webSearch.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<XAIXSearch>(webSearch.GetRawText());
    }


    public static XAIWebSearch? ToWebSearchTool(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(XAIRequestExtensions.XAIIdentifier, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("web_search", out var webSearch) || webSearch.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<XAIWebSearch>(webSearch.GetRawText());
    }

}
