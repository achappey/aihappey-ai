using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.AI;

public static class OpenAIResponsesStreamReader
{
    public static async IAsyncEnumerable<UIMessagePart> ReadAsync(
        this Stream stream,
        ChatRequest chatRequest,
        string modelFallback,
        IEnumerable<string>? providerTools = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream);

        string? eventType = null;
        string? streamId = null;
        string modelId = modelFallback;

        bool textStarted = false;
        bool reasoningStarted = false;
        // bool completed = false;

        int inputTokens = 0, outputTokens = 0, totalTokens = 0;
        int? reasoningTokens = null;

        var fullText = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (line.Length == 0)
            {
                eventType = null;
                continue;
            }

            if (line.StartsWith(":")) continue;

            if (line.StartsWith("event: "))
            {
                eventType = line["event: ".Length..].Trim();
                continue;
            }

            if (!line.StartsWith("data: ")) continue;

            var payload = line["data: ".Length..].Trim();
            if (payload is "[DONE]" or "[done]") break;

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            // ---- response envelope ----
            if (root.TryGetProperty("response", out var resp))
            {
                if (resp.TryGetProperty("id", out var idEl))
                    streamId ??= idEl.GetString();

                if (resp.TryGetProperty("model", out var mEl))
                    modelId = mEl.GetString() ?? modelId;

                ReadUsage(resp, ref inputTokens, ref outputTokens, ref totalTokens, ref reasoningTokens);
            }

            // ---- events ----
            switch (eventType)
            {
                case "response.output_text.delta":
                case "response.text.delta":
                    {
                        var delta = TryGetDelta(root);
                        if (delta is null || streamId is null) break;

                        if (!textStarted)
                        {
                            yield return streamId.ToTextStartUIMessageStreamPart();
                            textStarted = true;
                        }

                        fullText.Append(delta);
                        yield return new TextDeltaUIMessageStreamPart { Id = streamId, Delta = delta };
                        break;
                    }

                case "response.reasoning_summary_text.delta":
                    {
                        var delta = TryGetDelta(root);
                        if (delta is null || streamId is null) break;

                        if (!reasoningStarted)
                        {
                            yield return new ReasoningStartUIPart { Id = streamId };
                            reasoningStarted = true;
                        }

                        yield return new ReasoningDeltaUIPart { Id = streamId, Delta = delta };
                        break;
                    }

                case "response.reasoning_summary_text.done":
                    {
                        if (streamId != null && reasoningStarted)
                            yield return new ReasoningEndUIPart { Id = streamId };

                        reasoningStarted = false;
                        break;
                    }

                case "response.output_item.done":
                    {
                        foreach (var part in ReadToolCallPart(root, providerTools ?? [], chatRequest.Tools ?? []))
                            yield return part;
                        break;
                    }

                case "response.completed":
                case "response.completed.success":
                    {
                        //completed = true;
                        break;
                    }

                case "response.error":
                    {
                        var msg = root.TryGetProperty("error", out var e)
                            && e.TryGetProperty("message", out var m)
                            ? m.GetString()
                            : "Unknown error";

                        yield return new ErrorUIPart { ErrorText = msg ?? "Unknown error" };
                        break;
                    }
            }
        }

        // ---- finalize ----
        if (streamId != null && textStarted)
            yield return new TextEndUIMessageStreamPart { Id = streamId };

        if (chatRequest.ResponseFormat != null && fullText.Length > 0)
        {
            var schema = chatRequest.ResponseFormat.GetJSONSchema();
            var obj = JsonSerializer.Deserialize<object>(fullText.ToString());

            if (obj != null)
            {
                yield return new DataUIPart
                {
                    Type = $"data-{schema?.JsonSchema?.Name ?? "unknown"}",
                    Data = obj
                };
            }
        }

        yield return "stop".ToFinishUIPart(
            modelId,
            outputTokens,
            inputTokens,
            totalTokens,
            chatRequest.Temperature,
            reasoningTokens: reasoningTokens
        );
    }

    private static string? TryGetDelta(JsonElement root)
    {
        if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
            return d.GetString();

        if (root.TryGetProperty("output_text", out var ot)
            && ot.TryGetProperty("delta", out var d2))
            return d2.GetString();

        return null;
    }

    private static void ReadUsage(
     JsonElement resp,
     ref int inTok,
     ref int outTok,
     ref int totalTok,
     ref int? reasoningTok)
    {
        if (!resp.TryGetProperty("usage", out var u))
            return;

        if (u.ValueKind != JsonValueKind.Object)
            return;

        if (u.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number)
            inTok = i.GetInt32();

        if (u.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number)
            outTok = o.GetInt32();

        if (u.TryGetProperty("total_tokens", out var t) && t.ValueKind == JsonValueKind.Number)
            totalTok = t.GetInt32();

        if (u.TryGetProperty("reasoning_tokens", out var r) && r.ValueKind == JsonValueKind.Number)
            reasoningTok = r.GetInt32();
    }

    private static IEnumerable<UIMessagePart> ReadToolCallPart(JsonElement el, IEnumerable<string> providerTools, IEnumerable<Tool> tools)
    {
        if (el.ValueKind != JsonValueKind.Object)
            yield break;

        if (!el.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "response.output_item.done")
            yield break;

        if (!el.TryGetProperty("item", out var itemEl) || itemEl.ValueKind != JsonValueKind.Object)
            yield break;

        string? toolCallId = null;
        string? toolName = null;
        string? input = null;

        if (itemEl.TryGetProperty("id", out var idEl))
            toolCallId = idEl.GetString();

        if (itemEl.TryGetProperty("name", out var nameEl))
            toolName = nameEl.GetString();

        if (itemEl.TryGetProperty("arguments", out var argsEl))
            input = argsEl.GetString();

        if (string.IsNullOrEmpty(toolCallId) || string.IsNullOrEmpty(toolName))
            yield break;

        var providerExecuted = providerTools.Contains(toolName);

        // 1️⃣ Tool call (execution started)
        yield return new ToolCallPart
        {
            ToolCallId = toolCallId,
            ToolName = toolName,
            Title = tools?.FirstOrDefault(a => a.Name == toolName)?.Title,
            ProviderExecuted = providerTools.Contains(toolName),
            Input = !string.IsNullOrEmpty(input)
                ? JsonSerializer.Deserialize<object>(input!)!
                : new object()
        };

        if (providerExecuted)
        {
            // 2️⃣ Tool output (execution completed)
            yield return new ToolOutputAvailablePart
            {
                ToolCallId = toolCallId,
                ProviderExecuted = providerExecuted,
                Output = new ModelContextProtocol.Protocol.CallToolResult()
                {
                    IsError = false,
                    Content = ["Server-side tool call outputs are not returned in the API response. The agent uses these outputs internally to formulate its final response, but they are not exposed here.".ToTextContentBlock()]
                }
            };
        }
        else
        {
            yield return new ToolApprovalRequestPart
            {
                ToolCallId = toolCallId,
                ApprovalId = Guid.NewGuid().ToString(),
            };
        }
    }



}
