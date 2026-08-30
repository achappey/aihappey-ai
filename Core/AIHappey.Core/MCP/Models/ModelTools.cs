using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.Models;

[McpServerToolType]
public class ModelTools
{
    [Description("List available AI models with pagination.")]
    [McpServerTool(
        Title = "AI models",
        Name = "ai_models_list",
        Idempotent = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(Core.Models.ModelResponse),
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIModels_List(
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        [Description("Page number, starting at 1.")] int page = 1,
        [Description("Number of models per page. Default is 100.")] int pageSize = 100,
        [Description("Optional model type filter. Examples: language, embedding, reranking, image, video, speech, transcription.")]
        string? type = null,
        [Description("Optional provider id filter. Examples: openai, anthropic, google, groq, spacexai, etc.")]
        string? providerId = null,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IAIModelProviderResolver>();
        var models = await s.ResolveModels(ct);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 1000);

        if (!string.IsNullOrWhiteSpace(type))
        {
            models.Data = models.Data.Where(x =>
                string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(providerId))
        {
            models.Data = models.Data.Where(x =>
                x.Id.StartsWith($"{providerId}/", StringComparison.OrdinalIgnoreCase));
        }

        var res = models.Data
            .Skip((page - 1) * pageSize)
            .Take(pageSize);

        return new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new ModelResponse()
            {
                Data = res
            }, JsonOptions)
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
