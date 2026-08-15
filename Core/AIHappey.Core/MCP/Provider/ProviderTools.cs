using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.AI;
using System.Text.Json.Serialization;

namespace AIHappey.Core.MCP.Provider;

[McpServerToolType]
public class ProviderTools
{
    [Description("List all available AI provider identifiers.")]
    [McpServerTool(
        Title = "List AI providers",
        Name = "ai_providers_list",
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIProviders_List(
        IServiceProvider services,
        RequestContext<CallToolRequestParams> requestContext) =>
         await requestContext.WithExceptionCheck(async () =>

        {
            var providers = services.GetServices<IModelProvider>();

            return await Task.FromResult(new CallToolResult()
            {
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    providers = providers.Select(a => a.GetIdentifier())
                }, JsonOptions)
            });
        });

    [Description("Get AI models from all providers.")]
    [McpServerTool(Title = "Get AI models",
        Name = "ai_provider_get_models",
        Idempotent = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ModelResponse),
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIProvider_GetModels(
        [Description("Provider identifier")] string providerId,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) =>
         await requestContext.WithExceptionCheck(async () =>
    {
        var resolver = services.GetRequiredService<IAIModelProviderResolver>();
        var allModels = await resolver.ResolveModels(cancellationToken);
        var models = allModels.Data.Where(a => a.Id.StartsWith($"{providerId}/"));

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new ModelResponse()
            {
                Data = models
            }, JsonOptions)
        };
    });

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
