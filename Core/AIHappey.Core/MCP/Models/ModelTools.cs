using System.ComponentModel;
using System.Net.Mime;
using System.Text.Json;
using AIHappey.Core.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.Models;

[McpServerToolType]
public class ModelTools
{
    [Description("List all available models.")]
    [McpServerTool(Title = "AI models", Name = "ai_models_list", Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<ContentBlock?> AIModels_List(
           IServiceProvider services,
           RequestContext<CallToolRequestParams> _,
           CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IAIModelProviderResolver>();
        var res = await s.ResolveModels(ct);

        return new EmbeddedResourceBlock()
        {
            Resource = new TextResourceContents()
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://models",
                Text = JsonSerializer.Serialize(res, JsonSerializerOptions.Web)
            }
        };
    }
}
