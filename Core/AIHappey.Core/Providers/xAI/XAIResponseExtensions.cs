using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model.Providers.XAI;

namespace AIHappey.Core.Providers.xAI;

public static partial class XAIResponseExtensions
{
    public static XAIReasoning? ToReasoning(this JsonObject? obj)
    {
        if (obj is null)
            return null;

        if (obj[XAIRequestExtensions.XAIIdentifier] is not JsonObject provider)
            return null;

        if (provider["reasoning"] is not JsonObject reasoning)
            return null;

        return JsonSerializer.Deserialize<XAIReasoning>(reasoning.ToJsonString());
    }

    public static XAIXCodeExecution? ToCodeExecution(this JsonObject? obj)
    {
        if (obj is null)
            return null;

        if (obj[XAIRequestExtensions.XAIIdentifier] is not JsonObject provider)
            return null;

        if (provider["code_execution"] is not JsonObject codeExecution)
            return null;

        return JsonSerializer.Deserialize<XAIXCodeExecution>(codeExecution.ToJsonString());
    }

    public static XAIXSearch? ToXSearchTool(this JsonObject? obj)
    {
        if (obj is null)
            return null;

        if (obj[XAIRequestExtensions.XAIIdentifier] is not JsonObject provider)
            return null;

        if (provider["x_search"] is not JsonObject xSearch)
            return null;

        return JsonSerializer.Deserialize<XAIXSearch>(xSearch.ToJsonString());
    }

    public static XAIWebSearch? ToWebSearchTool(this JsonObject? obj)
    {
        if (obj is null)
            return null;

        if (obj[XAIRequestExtensions.XAIIdentifier] is not JsonObject provider)
            return null;

        if (provider["web_search"] is not JsonObject webSearch)
            return null;

        return JsonSerializer.Deserialize<XAIWebSearch>(webSearch.ToJsonString());
    }

}
