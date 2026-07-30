using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AIHappey.Core.Contracts;

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
        IServiceProvider services)
    {
        var providers = services.GetServices<IModelProvider>();

        return await Task.FromResult(new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                providers = providers.Select(a => a.GetIdentifier())
            }, JsonSerializerOptions.Web)
        });
    }

    [Description("Get AI models from all providers.")]
    [McpServerTool(Title = "Get AI models",
        Name = "ai_provider_get_models",
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIProvider_GetModels(
        [Description("Provider identifier")] string providerId,
         IServiceProvider services,
         CancellationToken cancellationToken)
    {
        var resolver = services.GetRequiredService<IAIModelProviderResolver>();
        var provider = await resolver.Resolve(providerId, cancellationToken);
        var models = await provider.ListModels(cancellationToken);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                data = models
            }, JsonSerializerOptions.Web)
        };
    }
}
