using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    [Description("Perform sourced web research. Ask the user for enough detail to form a precise topic before calling this tool.")]
    [McpServerTool(
        Title = "Perform web research",
        Name = "openai_web_research_run",
        ReadOnly = true,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true)]
    public async Task<CallToolResult> OpenAIWebResearch_Run(
        [Description("Precise research topic or question.")] string topic,
        RequestContext<CallToolRequestParams> context,
        IServiceProvider services,
        [Description("OpenAI model.")] string model = DefaultResearchModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        await SendResearchProgressAsync(context, 1, null, "Planning web research", cancellationToken);
        var planText = await RunResearchResponseAsync(
            services,
            model,
            "Create a concise web research plan. Return only JSON matching {\"queries\":[{\"query\":\"...\",\"reason\":\"...\"}]}. Produce 3 to 6 distinct, high-value searches.",
            topic,
            "medium",
            webSearch: false,
            cancellationToken);
        var plan = ParseResearchPlan(planText, topic);
        var total = plan.Searches.Count + 2;

        await SendResearchProgressAsync(
            context,
            2,
            total,
            $"Running {plan.Searches.Count} evidence searches",
            cancellationToken);

        var evidenceTasks = plan.Searches.Select(async (search, index) =>
        {
            await SendResearchProgressAsync(
                context,
                index + 2,
                total,
                $"Searching: {search.Query}",
                cancellationToken);

            return await RunResearchResponseAsync(
                services,
                model,
                "Research the query using web search. Return concise sourced notes with dates, direct source URLs, relevant quotations or figures, and explicit uncertainty. Do not invent sources.",
                $"Original topic: {topic}\nSearch query: {search.Query}\nReason: {search.Reason}",
                "low",
                webSearch: true,
                cancellationToken);
        });

        var evidence = await Task.WhenAll(evidenceTasks);
        await SendResearchProgressAsync(context, total, total, "Writing sourced report", cancellationToken);

        var report = await RunResearchResponseAsync(
            services,
            model,
            "Write a clear research report answering the original topic from the supplied evidence. Preserve inline source URLs, distinguish facts from inference, mention material conflicts or uncertainty, and finish with a Sources section. Never create sources not present in the evidence.",
            $"Original topic:\n{topic}\n\nEvidence:\n{string.Join("\n\n---\n\n", evidence)}",
            "medium",
            webSearch: false,
            cancellationToken);

        return ToTextToolResult(report);
    }
}
