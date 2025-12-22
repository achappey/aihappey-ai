using System.ComponentModel;
using System.Net.Mime;
using System.Text.Json;
using AIHappey.Telemetry;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.AzureAuth.MCP.Tools;

[McpServerToolType]
public class ToolTools
{
    // -------------------------
    // Helpers
    // -------------------------
    private static StatsWindow Days(int days) => StatsWindow.LastDaysUtc(days <= 0 ? 1 : days);

    private static TopOrder ParseOrder(string? order) =>
        string.Equals(order, "tokens", StringComparison.OrdinalIgnoreCase) ? TopOrder.Tokens : TopOrder.Requests;

    // -------------------------
    // TELEMETRY: Top Tools
    // -------------------------
    [Description("Top tools by requests or tokens.")]
    [McpServerTool(Title = "Telemetry top tools", Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<ContentBlock?> Telemetry_TopTools(
        [Description("Lookback window in days (UTC).")] int days,
        [Description("Max items to return.")] int top,
        [Description("Order by 'requests' (default) or 'tokens'.")] string? order,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.TopToolsAsync(Days(days), Math.Max(1, top), ParseOrder(order), ct);
        return new EmbeddedResourceBlock()
        {
            Resource = new TextResourceContents()
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://top/tools",
                Text = JsonSerializer.Serialize(res, JsonSerializerOptions.Web)
            }
        };
    }
}
