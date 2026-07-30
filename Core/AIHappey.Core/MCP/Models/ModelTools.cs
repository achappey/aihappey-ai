using System.ComponentModel;
using System.Text.Json;
using AIHappey.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.Models;

[McpServerToolType]
public class ModelTools
{
    [Description("List all available models.")]
    [McpServerTool(Title = "AI models",
        Name = "ai_models_list",
        Idempotent = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(Core.Models.ModelResponse),
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIModels_List(
           IServiceProvider services,
           RequestContext<CallToolRequestParams> _,
           CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IAIModelProviderResolver>();
        var res = await s.ResolveModels(ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(res, JsonSerializerOptions.Web)
        };
    }
}
