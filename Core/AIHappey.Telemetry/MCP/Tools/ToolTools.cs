using System.ComponentModel;
using System.Net.Mime;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Telemetry.MCP.Tools;

[McpServerToolType]
public class ToolTools
{
    // Helpers
    private static StatsWindow Range(DateTime startDateTimeUtc, DateTime? endDateTimeUtc)
    {
        if (startDateTimeUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("startDateTimeUtc must be provided in UTC.", nameof(startDateTimeUtc));

        var end = endDateTimeUtc ?? DateTime.UtcNow;
        if (end.Kind != DateTimeKind.Utc)
            throw new ArgumentException("endDateTimeUtc must be provided in UTC when specified.", nameof(endDateTimeUtc));

        if (end <= startDateTimeUtc)
            throw new ArgumentException("endDateTimeUtc must be greater than startDateTimeUtc.", nameof(endDateTimeUtc));

        return new StatsWindow(startDateTimeUtc, end);
    }

    private static TopOrder ParseOrder(string? order) =>
        string.Equals(order, "tokens", StringComparison.OrdinalIgnoreCase) ? TopOrder.Tokens : TopOrder.Requests;

    // -------------------------
    // TELEMETRY: Top Tools
    // -------------------------
    [Description("Top tools by requests or tokens.")]
    [McpServerTool(Title = "Telemetry top tools", Name = "ai_tools_top_tools", Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> Telemetry_TopTools(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        [Description("Max items to return.")] int top,
        [Description("Order by 'requests' (default) or 'tokens'.")] string? order,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.TopToolsAsync(Range(startDateTimeUtc, endDateTimeUtc), Math.Max(1, top), ParseOrder(order), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { items = res }, JsonSerializerOptions.Web)
        };
    }
}
