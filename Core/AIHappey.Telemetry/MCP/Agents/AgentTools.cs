using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Telemetry.MCP.Agents;

[McpServerToolType]
public class AgentTools
{
    // -------------------------
    // Helpers
    // -------------------------
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
        string.Equals(order, "tokens", StringComparison.OrdinalIgnoreCase) ? TopOrder.Tokens :
        string.Equals(order, "duration", StringComparison.OrdinalIgnoreCase) ? TopOrder.Duration
            : TopOrder.Requests;

    // -------------------------
    // TELEMETRY: Agent Overview
    // -------------------------
    [Description("High-level agent telemetry only: requests, active agents, users, models, avg latency, token sums. Requests without an agent id are ignored.")]
    [McpServerTool(Title = "Agent telemetry overview", Name = "ai_agents_overview",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIAgents_Overview(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetAgentOverviewAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(res, JsonSerializerOptions.Web)
        };
    }

    // -------------------------
    // TELEMETRY: Agent Daily Activity
    // -------------------------
    [Description("Daily agent buckets: requests, unique agents, unique users, total tokens. Requests without an agent id are ignored.")]
    [McpServerTool(Title = "Agent telemetry daily activity", Name = "ai_agents_daily_activity",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIAgents_DailyActivity(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetAgentDailyActivityAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { items = res }, JsonSerializerOptions.Web)
        };
    }

    // -------------------------
    // TELEMETRY: Top Agents
    // -------------------------
    [Description("Top agents by requests, tokens or duration. Requests without an agent id are ignored.")]
    [McpServerTool(Title = "Telemetry top agents", Name = "ai_agents_top_agents",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIAgents_TopAgents(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        [Description("Max items to return.")] int top,
        [Description("Order by 'requests' (default), 'tokens' or 'duration'.")] string? order,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.TopAgentsAsync(Range(startDateTimeUtc, endDateTimeUtc), Math.Max(1, top), ParseOrder(order), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { items = res }, JsonSerializerOptions.Web)
        };
    }

    // -------------------------
    // TELEMETRY: Agent Request Type Breakdown
    // -------------------------
    [Description("Counts per request type for agent requests only. Requests without an agent id are ignored.")]
    [McpServerTool(Title = "Agent telemetry request types", Name = "ai_agents_request_types",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIAgents_RequestTypes(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetAgentRequestTypesAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(res, JsonSerializerOptions.Web)
        };
    }

    // -------------------------
    // TELEMETRY: Top Models For Agent
    // -------------------------
    [Description("For a specific agent id, rank the models used by requests, tokens or duration (seconds).")]
    [McpServerTool(Title = "Agent top models", Name = "ai_agents_top_models_for_agent",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIAgents_TopModelsForAgent(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        [Description("Exact agent id to inspect. Matching is case-insensitive after trimming.")] string agentId,
        [Description("Max items to return.")] int top,
        [Description("Order by 'requests' (default), 'tokens' or 'duration'.")] string? order,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.TopModelsForAgentAsync(Range(startDateTimeUtc, endDateTimeUtc), agentId, Math.Max(1, top), ParseOrder(order), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { items = res }, JsonSerializerOptions.Web)
        };
    }

    // -------------------------
    // TELEMETRY: Top Users For Agent
    // -------------------------
    [Description("For a specific agent id, rank the users sending requests through that agent by requests, tokens or duration (seconds).")]
    [McpServerTool(Title = "Agent top users", Name = "ai_agents_top_users_for_agent",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIAgents_TopUsersForAgent(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        [Description("Exact agent id to inspect. Matching is case-insensitive after trimming.")] string agentId,
        [Description("Max items to return.")] int top,
        [Description("Order by 'requests' (default), 'tokens' or 'duration'.")] string? order,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.TopUsersForAgentAsync(Range(startDateTimeUtc, endDateTimeUtc), agentId, Math.Max(1, top), ParseOrder(order), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { items = res }, JsonSerializerOptions.Web)
        };
    }

    // -------------------------
    // TELEMETRY: Agent Token Stats
    // -------------------------
    [Description("Token stats for agent requests only: min, p50, p95, max, average for input & total. Requests without an agent id are ignored.")]
    [McpServerTool(Title = "Agent telemetry token stats", Name = "ai_agents_token_stats",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIAgents_TokenStats(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetAgentTokenStatsAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(res, JsonSerializerOptions.Web)
        };
    }

    // -------------------------
    // TELEMETRY: Agent Latency Stats
    // -------------------------
    [Description("Latency stats for agent requests only: min, p50, p95, max, average (ms). Requests without an agent id are ignored.")]
    [McpServerTool(Title = "Agent telemetry latency stats", Name = "ai_agents_latency_stats",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIAgents_LatencyStats(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetAgentLatencyStatsAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(res, JsonSerializerOptions.Web)
        };
    }
}
