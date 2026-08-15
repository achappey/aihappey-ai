using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            StructuredContent = JsonSerializer.SerializeToElement(res, JsonOptions)
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
