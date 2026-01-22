using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.Realtime;

[McpServerToolType]
public class RealtimeTools
{
    [Description("Get a realtime token/session descriptor for a Vercel-compatible realtime endpoint. Returns the raw token output as structured content.")]
    [McpServerTool(
        Title = "Get realtime token",
        Name = "ai_realtime_token_get",
        Idempotent = false,
        ReadOnly = false,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AI_RealtimeTokenGet(
        [Description("AI model identifier")] string model,
        RequestContext<CallToolRequestParams> requestContext,
        IServiceProvider services,
        CancellationToken ct = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("'model' is required.");

            var resolver = services.GetRequiredService<IAIModelProviderResolver>();
            var provider = await resolver.Resolve(model, ct);

            var req = new RealtimeRequest { Model = model.SplitModelId().Model, ProviderOptions = null };
            var result = await provider.GetRealtimeToken(req, ct);

            var structured = new JsonObject
            {
                ["value"] = result.Value,
                ["expires_at"] = result.ExpiresAt,
                ["providerMetadata"] = JsonSerializer.SerializeToNode(result.ProviderMetadata, JsonSerializerOptions.Web)
            };

            return new CallToolResult { StructuredContent = structured };
        });
}

