using System.ComponentModel;
using System.Net.Mime;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Telemetry;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.AzureAuth.MCP.Models;

[McpServerToolType]
public class ModelTools
{
    // -------------------------
    // Helpers
    // -------------------------
    private static StatsWindow Days(int days) => StatsWindow.LastDaysUtc(days <= 0 ? 1 : days);

    private static TopOrder ParseOrder(string? order) =>
        string.Equals(order, "tokens", StringComparison.OrdinalIgnoreCase) ? TopOrder.Tokens : TopOrder.Requests;

    [Description("List all available models.")]
    [McpServerTool(Title = "AI models", Name = "ai_models_list", Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<ContentBlock?> AIModels_List(
           IServiceProvider services,
           RequestContext<CallToolRequestParams> _,
           CancellationToken ct = default)
    {
        var s = services.GetRequiredService<AIModelProviderResolver>();
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

    // -------------------------
    // TELEMETRY: Top Models
    // -------------------------
    [Description("Top models by requests or tokens.")]
    [McpServerTool(Title = "Telemetry top models", Name = "ai_models_top_models", Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<ContentBlock?> AIModels_TopModels(
        [Description("Lookback window in days (UTC).")] int days,
        [Description("Max items to return.")] int top,
        [Description("Order by 'requests' (default) or 'tokens'.")] string? order,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.TopModelsAsync(Days(days), Math.Max(1, top), ParseOrder(order), ct);
        return new EmbeddedResourceBlock()
        {
            Resource = new TextResourceContents()
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://top/models",
                Text = JsonSerializer.Serialize(res, JsonSerializerOptions.Web)
            }
        };
    }

    // -------------------------
    // TELEMETRY: Top Providers
    // -------------------------
    [Description("Top providers by requests or tokens.")]
    [McpServerTool(Title = "Telemetry top providers", Name = "ai_models_top_providers", 
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<ContentBlock?> AIModels_TopProviders(
        [Description("Lookback window in days (UTC).")] int days,
        [Description("Max items to return.")] int top,
        [Description("Order by 'requests' (default) or 'tokens'.")] string? order,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.TopProvidersAsync(Days(days), Math.Max(1, top), ParseOrder(order), ct);
        return new EmbeddedResourceBlock()
        {
            Resource = new TextResourceContents()
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://top/providers",
                Text = JsonSerializer.Serialize(res, JsonSerializerOptions.Web)
            }
        };
    }
}
