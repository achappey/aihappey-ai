using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    [Description("Perform sourced financial research with separate fundamentals, risks, report, and verification stages. This is research, not personalized financial advice.")]
    [McpServerTool(
        Title = "Perform financial research",
        Name = "openai_financial_research_run",
        ReadOnly = true,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true)]
    public async Task<CallToolResult> OpenAIFinancialResearch_Run(
        [Description("Precise financial research subject or question.")] string topic,
        RequestContext<CallToolRequestParams> context,
        IServiceProvider services,
        [Description("OpenAI model.")] string model = DefaultResearchModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        await SendResearchProgressAsync(context, 1, null, "Planning financial research", cancellationToken);
        var planText = await RunResearchResponseAsync(
            services,
            model,
            "Plan rigorous financial research. Return only JSON matching {\"queries\":[{\"query\":\"...\",\"reason\":\"...\"}]}. Produce 4 to 8 searches covering primary filings, current financials, valuation, catalysts, industry context, and material risks as applicable.",
            topic,
            "medium",
            webSearch: false,
            cancellationToken);
        var plan = ParseResearchPlan(planText, topic);
        var total = plan.Searches.Count + 5;

        var evidenceTasks = plan.Searches.Select(async (search, index) =>
        {
            await SendResearchProgressAsync(context, index + 2, total, $"Searching: {search.Query}", cancellationToken);
            return await RunResearchResponseAsync(
                services,
                model,
                "Use web search for financial evidence. Prefer regulatory filings, investor relations, official statistics, exchanges, and reputable financial reporting. Include dates, periods, units, direct URLs, and uncertainty. Never invent a figure or source.",
                $"Original question: {topic}\nQuery: {search.Query}\nReason: {search.Reason}",
                "low",
                webSearch: true,
                cancellationToken);
        });

        var evidence = await Task.WhenAll(evidenceTasks);
        var evidenceText = string.Join("\n\n---\n\n", evidence);
        var step = plan.Searches.Count + 2;

        await SendResearchProgressAsync(context, step++, total, "Analyzing fundamentals", cancellationToken);
        var fundamentalsTask = RunResearchResponseAsync(
            services,
            model,
            "Analyze the supplied evidence for financial fundamentals. Cover growth, margins, cash flow, balance sheet, capital allocation, valuation assumptions, and peer context when relevant. Use dated figures and retain source URLs. State missing data.",
            $"Question: {topic}\n\nEvidence:\n{evidenceText}",
            "medium",
            webSearch: false,
            cancellationToken);

        await SendResearchProgressAsync(context, step++, total, "Analyzing risks", cancellationToken);
        var risksTask = RunResearchResponseAsync(
            services,
            model,
            "Analyze the supplied evidence for downside risks, counterarguments, concentration, regulation, competition, execution, accounting quality, liquidity, and scenario sensitivity as relevant. Retain source URLs and state missing evidence.",
            $"Question: {topic}\n\nEvidence:\n{evidenceText}",
            "medium",
            webSearch: false,
            cancellationToken);

        await Task.WhenAll(fundamentalsTask, risksTask);
        var fundamentals = await fundamentalsTask;
        var risks = await risksTask;

        await SendResearchProgressAsync(context, step++, total, "Writing financial report", cancellationToken);
        var report = await RunResearchResponseAsync(
            services,
            model,
            "Write a balanced, decision-useful financial research report. Answer the question, separate facts from assumptions, include bull/base/bear considerations where useful, preserve source URLs, and clearly say this is research rather than personalized financial advice.",
            $"Question: {topic}\n\nFundamentals:\n{fundamentals}\n\nRisks:\n{risks}\n\nSourced evidence:\n{evidenceText}",
            "medium",
            webSearch: false,
            cancellationToken);

        await SendResearchProgressAsync(context, step, total, "Verifying claims and sources", cancellationToken);
        var verification = await RunResearchResponseAsync(
            services,
            model,
            "Audit the draft against the supplied evidence. List unsupported, stale, internally inconsistent, or overconfident claims and provide precise corrections. Do not introduce new facts or sources. If no material issues exist, say so explicitly.",
            $"Question: {topic}\n\nDraft report:\n{report}\n\nEvidence:\n{evidenceText}",
            "medium",
            webSearch: false,
            cancellationToken);

        return ToTextToolResult($"{report}\n\n## Verification\n\n{verification}");
    }
}
