using System.Dynamic;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.XAI;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.xAI;

public static partial class XAIRequestExtensions
{
    public const string XAIIdentifier = "xai";

    public static HttpRequestMessage BuildXAIStreamRequest(
            this ChatRequest chatRequest,
            string providerIdentifier)
    {
        var tools = new List<dynamic>();

        var metadata = chatRequest.GetProviderMetadata<XAIProviderMetadata>(providerIdentifier);

        if (metadata?.XSearch != null)
            tools.Add(metadata.XSearch);

        if (metadata?.WebSearch != null)
            tools.Add(metadata.WebSearch);

        if (metadata?.CodeExecution != null)
            tools.Add(metadata.CodeExecution);

        foreach (var tool in chatRequest.Tools ?? [])
        {
            tools.Add(new
            {
                type = "function",
                name = tool.Name,
                description = tool.Description,
                parameters = tool.InputSchema
            });
        }

        dynamic payload = new ExpandoObject();
        payload.model = chatRequest.Model;
        payload.stream = true;
        payload.temperature = chatRequest.Temperature;
        payload.reasoning = metadata?.Reasoning;
        payload.instructions = metadata?.Instructions;
        payload.store = false;
        payload.parallel_tool_calls = metadata?.ParallelToolCalls;
        payload.input = chatRequest.Messages.BuildResponsesInput();
        payload.tools = tools;

        if (chatRequest.ResponseFormat != null)
        {
            payload.text = new
            {
                format = chatRequest.ResponseFormat
            };
        }

        if (tools.Count > 0)
            payload.tool_choice = "auto";

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        return new HttpRequestMessage(HttpMethod.Post, "v1/responses")
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
    }


    public static List<dynamic> GetTools(this CreateMessageRequestParams chatRequest)
    {

        List<dynamic> allTools = [];
        XAIWebSearch? searchTool = chatRequest.Metadata.ToWebSearchTool();
        if (searchTool != null)
        {
            allTools.Add(searchTool);
        }

        XAIXSearch? xSearch = chatRequest.Metadata.ToXSearchTool();
        if (xSearch != null)
        {
            allTools.Add(xSearch);
        }

        XAIXCodeExecution? codeExecution = chatRequest.Metadata.ToCodeExecution();
        if (codeExecution != null)
        {
            allTools.Add(codeExecution);
        }

        return allTools;
    }


    public static Dictionary<string, object> ToProviderMetadata(this Dictionary<string, object> metadata)
        => new()
        { { XAIIdentifier, metadata } };
}
