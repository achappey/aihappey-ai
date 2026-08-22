using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Telemetry;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.Inference;

[McpServerToolType]
public class ResponsesTools
{
    [Description("Execute an AI request using the Responses endpoint. Each MCP progress notification contains the accumulated text for one response item while it streams.")]
    [McpServerTool(
        Title = "AI Responses",
        Name = "ai_responses_execute",
        Destructive = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ResponseResult),
        Idempotent = false,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIResponses_Execute(
        [Description("AI model identifier, including provider prefix.")] string model,
        [Description("Prompt to send to the model.")] string prompt,
        RequestContext<CallToolRequestParams> requestContext,
        IServiceProvider services,
        [Description("Optional instructions to guide the model response.")] string? instructions = null,
        [Description("Optional maximum number of output tokens.")] int? maxOutputTokens = null,
        CancellationToken ct = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            var (provider, providerModel) = await InferenceMcpHelpers.ResolveAsync(model, prompt, maxOutputTokens, services, ct);
            var startedAt = DateTime.UtcNow;

            var request = new ResponseRequest
            {
                Model = providerModel,
                Input = new ResponseInput(prompt),
                Instructions = instructions,
                MaxOutputTokens = maxOutputTokens,
                Store = false,
                Stream = true
            };

            ResponseResult? result = null;
            var progress = 1;
            var itemBuffers = new Dictionary<string, StringBuilder>();

            await foreach (var part in provider.ResponsesStreamingAsync(request, ct))
            {
                var completedItemMessage = GetCompletedItemMessage(part, itemBuffers);
                if (completedItemMessage is not null)
                {
                    await InferenceMcpHelpers.SendProgressAsync(requestContext, progress++, completedItemMessage);
                }

                if (part is ResponseCompleted completed)
                {
                    result = completed.Response;
                }
            }

            if (result is null)
                throw new InvalidOperationException("The inference stream completed without a 'response.completed' event.");

            await services.TrackMcpResponsesTelemetryAsync(result, provider, request.Temperature ?? 1, startedAt, ct);

            return new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(result, ResponseJson.Default)
            };
        });

    private static string? GetCompletedItemMessage(
        ResponseStreamPart part,
        Dictionary<string, StringBuilder> itemBuffers)
    {
        switch (part)
        {
            case ResponseReasoningTextDelta reasoningDelta:
                return AppendDelta(itemBuffers, GetItemKey("reasoning", reasoningDelta), reasoningDelta.Delta);

            case ResponseReasoningSummaryTextDelta summaryDelta:
                return AppendDelta(itemBuffers, GetItemKey("reasoning-summary", summaryDelta, summaryDelta.SummaryIndex), summaryDelta.Delta);

            case ResponseOutputTextDelta outputTextDelta:
                return AppendDelta(itemBuffers, GetItemKey(
                    "text",
                    outputTextDelta.ItemId,
                    outputTextDelta.Outputindex,
                    outputTextDelta.ContentIndex),
                    outputTextDelta.Delta);

            case ResponseRefusalDelta refusalDelta:
                return AppendDelta(itemBuffers, GetItemKey("refusal", refusalDelta), refusalDelta.Delta);

            case ResponseReasoningTextDone reasoningDone:
                return GetCompletedText(itemBuffers, GetItemKey("reasoning", reasoningDone), reasoningDone.Text);

            case ResponseReasoningSummaryTextDone summaryDone:
                return GetCompletedText(itemBuffers, GetItemKey("reasoning-summary", summaryDone, summaryDone.SummaryIndex), summaryDone.Text);

            case ResponseOutputTextDone outputTextDone:
                return GetCompletedText(itemBuffers, GetItemKey(
                    "text",
                    outputTextDone.ItemId,
                    outputTextDone.Outputindex,
                    outputTextDone.ContentIndex),
                    outputTextDone.Text);

            case ResponseRefusalDone refusalDone:
                return GetCompletedText(itemBuffers, GetItemKey("refusal", refusalDone), refusalDone.Refusal);

            default:
                return null;
        }
    }

    private static string? AppendDelta(Dictionary<string, StringBuilder> itemBuffers, string key, string? delta)
    {
        return InferenceMcpHelpers.Append(itemBuffers, key, delta);
    }

    private static string? GetCompletedText(
        Dictionary<string, StringBuilder> itemBuffers,
        string key,
        string? completedText)
    {
        itemBuffers.Remove(key, out var buffer);

        if (buffer is { Length: > 0 })
        {
            var accumulatedText = buffer.ToString();
            return !string.Equals(completedText, accumulatedText, StringComparison.Ordinal)
                ? completedText
                : null;
        }

        return completedText;
    }

    private static string GetItemKey(string kind, ResponseStreamItemContentEvent item, int? summaryIndex = null)
        => GetItemKey(kind, item.ItemId, item.OutputIndex, item.ContentIndex, summaryIndex);

    private static string GetItemKey(
        string kind,
        string itemId,
        int outputIndex,
        int contentIndex,
        int? summaryIndex = null)
        => $"{kind}:{itemId}:{outputIndex}:{contentIndex}:{summaryIndex}";




}

