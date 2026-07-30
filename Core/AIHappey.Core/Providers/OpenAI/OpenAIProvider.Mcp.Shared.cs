using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.MCP.Telemetry;
using AIHappey.Responses;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    private const string DefaultResearchModel = "gpt-5.6-luna";

    private async Task<string> RunResearchResponseAsync(
        IServiceProvider services,
        string model,
        string instructions,
        string input,
        string reasoningEffort,
        bool webSearch,
        CancellationToken cancellationToken)
    {
        var request = new ResponseRequest
        {
            Model = model,
            Instructions = instructions,
            Input = new ResponseInput(input),
            Store = false,
            Reasoning = new Reasoning { Effort = reasoningEffort },
            Tools = webSearch ? [CreateWebSearchTool()] : null
        };

        var startedAt = DateTime.UtcNow;
        var response = await ResponsesAsync(request, cancellationToken);
        await services.TrackMcpResponsesTelemetryAsync(
            response,
            this,
            request.Temperature ?? 1,
            startedAt,
            cancellationToken);

        var text = response.Output.GetAssistantOutputText();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("OpenAI returned no assistant text.");

        return text.Trim();
    }

    private static ResponseToolDefinition CreateWebSearchTool() => new()
    {
        Type = "web_search"
    };

    private static async Task SendResearchProgressAsync(
        RequestContext<CallToolRequestParams> context,
        int progress,
        int? total,
        string message,
        CancellationToken cancellationToken)
    {
        if (context.Params?.ProgressToken is not { } token)
            return;

        await context.Server.SendNotificationAsync(
            "notifications/progress",
            new ProgressNotificationParams
            {
                ProgressToken = token,
                Progress = new ProgressNotificationValue
                {
                    Progress = progress,
                    Total = total,
                    Message = message
                }
            },
            cancellationToken: cancellationToken);
    }

    private static ResearchPlan ParseResearchPlan(string json, string fallbackTopic)
    {
        try
        {
            var cleaned = CleanJson(json);
            var plan = JsonSerializer.Deserialize<ResearchPlan>(cleaned, JsonSerializerOptions.Web);
            if (plan?.Searches is { Count: > 0 })
            {
                plan.Searches = plan.Searches
                    .Where(item => !string.IsNullOrWhiteSpace(item.Query))
                    .Take(8)
                    .ToList();

                if (plan.Searches.Count > 0)
                    return plan;
            }
        }
        catch (JsonException)
        {
            // A deterministic fallback keeps research useful if a model wraps or
            // slightly malforms planner JSON.
        }

        return new ResearchPlan
        {
            Searches = [new ResearchSearch { Query = fallbackTopic, Reason = "Direct research query" }]
        };
    }

    private static string CleanJson(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
                trimmed = trimmed[(firstLine + 1)..lastFence].Trim();
        }

        var objectStart = trimmed.IndexOf('{');
        var objectEnd = trimmed.LastIndexOf('}');
        return objectStart >= 0 && objectEnd > objectStart
            ? trimmed[objectStart..(objectEnd + 1)]
            : trimmed;
    }

    private static CallToolResult ToTextToolResult(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }]
    };

    private sealed class ResearchPlan
    {
        [JsonPropertyName("queries")]
        public List<ResearchSearch> Searches { get; set; } = [];
    }

    private sealed class ResearchSearch
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }
}
