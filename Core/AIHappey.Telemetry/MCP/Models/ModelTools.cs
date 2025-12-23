using System.ComponentModel;
using System.Net.Mime;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Telemetry.MCP.Models;

[McpServerToolType]
public class ModelTools
{
    // Helpers (private)
    private static StatsWindow Days(int days) => StatsWindow.LastDaysUtc(days <= 0 ? 1 : days);

    private static TopOrder ParseOrder(string? order) =>
        string.Equals(order, "tokens", StringComparison.OrdinalIgnoreCase) ? TopOrder.Tokens : TopOrder.Requests;

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
