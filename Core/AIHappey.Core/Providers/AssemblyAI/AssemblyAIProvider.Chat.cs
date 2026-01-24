using AIHappey.Core.AI;
using AIHappey.Common.Model;
using AIHappey.Core.ModelProviders;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.AssemblyAI;

public partial class AssemblyAIProvider : IModelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
           ChatRequest chatRequest,
           [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(_llmGatewayClient);

        ArgumentNullException.ThrowIfNull(chatRequest);

        // AssemblyAI LLM Gateway is non-streaming; we simulate the UI stream.
        var streamId = Guid.NewGuid().ToString("n");

        var mappedMessages = chatRequest.Messages.ToAssemblyAIMessages()
            .ToList();

        var toolDefs = chatRequest.Tools?.Count > 0
            ? chatRequest.Tools.Select(t => (object)new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.InputSchema
                }
            }).ToList()
            : new List<object>();

        var options = new Dictionary<string, object?>
        {
            ["model"] = chatRequest.Model,
            ["parallel_tool_calls"] = true,
            ["messages"] = mappedMessages,
            ["tools"] = toolDefs,
            ["temperature"] = chatRequest.Temperature,
        };

        if (toolDefs.Count > 0)
            options["tool_choice"] = chatRequest.ToolChoice ?? "auto";

        if (chatRequest.ResponseFormat is not null)
            options["response_format"] = chatRequest.ResponseFormat;

        var completionResult = await _llmGatewayClient.GetChatCompletion(
            JsonSerializer.SerializeToElement(options),
            relativeUrl: "v1/chat/completions",
            ct: cancellationToken);

        // Parse first choice message content/tool_calls (choices are opaque objects)
        string? content = null;
        List<(string ToolCallId, string ToolName, object Input)> toolCalls = [];

        var firstChoice = completionResult.Choices?.FirstOrDefault();
        if (firstChoice is JsonElement chEl && chEl.ValueKind == JsonValueKind.Object)
        {
            if (chEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
            {
                if (msgEl.TryGetProperty("content", out var cEl))
                    content = cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : cEl.GetRawText();

                if (msgEl.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcEl.EnumerateArray())
                    {
                        if (tc.ValueKind != JsonValueKind.Object) continue;

                        var id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString()!
                            : Guid.NewGuid().ToString("n");

                        if (!tc.TryGetProperty("function", out var fnEl) || fnEl.ValueKind != JsonValueKind.Object)
                            continue;

                        var name = fnEl.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                            ? nEl.GetString()!
                            : "";

                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        // arguments may be JSON string or object; normalize to object
                        object inputObj = new { };
                        if (fnEl.TryGetProperty("arguments", out var aEl))
                        {
                            try
                            {
                                if (aEl.ValueKind == JsonValueKind.String)
                                {
                                    var rawArgs = aEl.GetString();
                                    inputObj = string.IsNullOrWhiteSpace(rawArgs)
                                        ? new { }
                                        : (JsonSerializer.Deserialize<object>(rawArgs!, JsonSerializerOptions.Web) ?? new { });
                                }
                                else if (aEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                                {
                                    inputObj = JsonSerializer.Deserialize<object>(aEl.GetRawText(), JsonSerializerOptions.Web) ?? new { };
                                }
                            }
                            catch
                            {
                                inputObj = new { value = aEl.ValueKind == JsonValueKind.String ? aEl.GetString() : aEl.GetRawText() };
                            }
                        }

                        toolCalls.Add((id, name, inputObj));
                    }
                }
            }
        }

        // Emit text stream (single delta) if any
        if (!string.IsNullOrWhiteSpace(content))
        {
            yield return streamId.ToTextStartUIMessageStreamPart();
            yield return new TextDeltaUIMessageStreamPart { Id = streamId, Delta = content! };
            yield return streamId.ToTextEndUIMessageStreamPart();
        }

        // Emit tool calls (simulated; no deltas)
        foreach (var tc in toolCalls)
        {
            yield return new ToolCallStreamingStartPart
            {
                ToolCallId = tc.ToolCallId,
                ToolName = tc.ToolName,
                Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == tc.ToolName)?.Title,
                ProviderExecuted = false
            };

            yield return new ToolCallPart
            {
                ToolCallId = tc.ToolCallId,
                ToolName = tc.ToolName,
                Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == tc.ToolName)?.Title,
                ProviderExecuted = false,
                Input = tc.Input
            };

            yield return new ToolApprovalRequestPart
            {
                ToolCallId = tc.ToolCallId,
                ApprovalId = Guid.NewGuid().ToString()
            };
        }

        // Usage is provider-specific; AssemblyAI uses input_tokens/output_tokens/total_tokens.
        int inputTokens = 0, outputTokens = 0, totalTokens = 0;
        if (completionResult.Usage is JsonElement usageEl && usageEl.ValueKind == JsonValueKind.Object)
        {
            if (usageEl.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number)
                inputTokens = it.GetInt32();
            if (usageEl.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number)
                outputTokens = ot.GetInt32();
            if (usageEl.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number)
                totalTokens = tt.GetInt32();
        }

        yield return "stop".ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: outputTokens,
            inputTokens: inputTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature);
    }


}


