using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Nodes;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using ModelContextProtocol.Protocol;
using AIHappey.Core.AI;
using AIHappey.Common.Model.Providers;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider : IModelProvider
{
    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
          ChatRequest chatRequest,
          [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = chatRequest.GetProviderMetadata<GroqProviderMetadata>(GetIdentifier());

        // --- 1️⃣ Build GROQ payload ---
        var tools = chatRequest.Tools?.Select(a => new
        {
            type = "function",
            name = a.Name,
            description = a.Description,
            parameters = a.InputSchema,
        }).Cast<object>().ToList() ?? [];

        var systemPrompt = string.Join("\n\n", chatRequest.Messages
            .Where(a => a.Role == Common.Model.Role.system)
            .SelectMany(a => a.Parts.OfType<TextUIPart>().Select(a => a.Text)));

        if (metadata?.BrowserSearch != null)
        {
            tools.Add(metadata.BrowserSearch);
        }

        if (metadata?.CodeInterpreter != null)
        {
            tools.Add(metadata.CodeInterpreter);
        }

        var payload = new
        {
            model = chatRequest.Model,
            stream = true,
            store = false,
            temperature = chatRequest.Temperature,
            parallel_tool_calls = metadata?.ParallelToolCalls,
            reasoning = metadata?.Reasoning,
            instructions = metadata?.Instructions,
            truncation = "auto",
            input = chatRequest.Messages.ToGroqMessages(),
            tools,
            tool_choice = "auto"
        };

        // --- 2️⃣ Send SSE request ---
        using var req = new HttpRequestMessage(HttpMethod.Post, "openai/v1/responses")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            yield return $"Groq stream error: {(string.IsNullOrWhiteSpace(err) ? resp.ReasonPhrase : err)}"
                .ToErrorUIPart();

            yield break;
        }

        // --- 3️⃣ Stream tokens ---
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? streamId = null;
        bool textStarted = false;
        // bool sawDone = false;
        string modelId = chatRequest.Model;
        // string? reasoningId = null;
        string? currentToolId = null;
        StringBuilder? currentToolArgs = null;
        string? reasoningId = null;
        bool reasoningStarted = false;
        string? currentToolName = null;

        int? inputTokens = null, outputTokens = null, totalTokens = null, reasoningTokens = null;
        var activeReasoning = new HashSet<string>();

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;
            if (line.Length == 0) continue;
            if (line.StartsWith(":")) continue;

            if (!line.StartsWith("data: ")) continue;
            var jsonData = line["data: ".Length..].Trim();
            if (string.IsNullOrEmpty(jsonData) || jsonData == "[DONE]")
            {
                break;
            }

            JsonNode? node = JsonNode.Parse(jsonData);

            var type = node?["type"]?.GetValue<string>() ?? "";

            // 🔹 Stream deltas as before
            if (type == "response.output_text.delta" || type == "output_text.delta" || type == "message.delta")
            {
                var delta = node?["delta"]?.GetValue<string>()
                            ?? node?["text"]?.GetValue<string>()
                            ?? node?["content"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(delta))
                {
                    streamId ??= node?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
                    if (!textStarted)
                    {
                        yield return streamId.ToTextStartUIMessageStreamPart();
                        textStarted = true;
                    }

                    yield return new TextDeltaUIMessageStreamPart
                    {
                        Id = streamId,
                        Delta = delta
                    };
                }
            }

            // 🔹 Capture totals when response completes
            else if (type == "response.completed")
            {
                //sawDone = true;

                var usage = node?["response"]?["usage"];
                if (usage != null)
                {
                    inputTokens = usage["input_tokens"]?.GetValue<int?>();
                    outputTokens = usage["output_tokens"]?.GetValue<int?>();
                    reasoningTokens = usage["output_tokens_details"]?["reasoning_tokens"]?.GetValue<int?>();
                    totalTokens = usage["total_tokens"]?.GetValue<int?>();
                }
                break;
            }// 🔹 Handle reasoning text
             // 🔹 Handle reasoning text (streamed as delta + done)

            else if (type == "response.reasoning_text.delta")
            {
                var reasoningDelta = node?["delta"]?.GetValue<string>()
                                   ?? node?["text"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(reasoningDelta))
                {
                    if (!reasoningStarted)
                    {
                        reasoningId = Guid.NewGuid().ToString("n");
                        reasoningStarted = true;

                        yield return new ReasoningStartUIPart
                        {
                            Id = reasoningId
                        };
                    }

                    yield return new ReasoningDeltaUIPart
                    {
                        Id = reasoningId!,
                        Delta = reasoningDelta
                    };
                }
            }
            else if (type == "response.reasoning_text.done")
            {
                if (reasoningStarted && reasoningId is not null)
                {
                    yield return new ReasoningEndUIPart
                    {
                        Id = reasoningId
                    };

                    reasoningStarted = false;
                    reasoningId = null;
                }
            }
            // 🔹 Handle MCP tool call lifecycle (browser.search, code.interpreter, etc.)
            else if (type == "response.output_item.added")
            {
                var item = node?["item"];
                if (item?["type"]?.GetValue<string>() == "mcp_call")
                {
                    var toolId = item?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
                    currentToolName = item?["name"]?.GetValue<string>() ?? "unknown_tool";

                    yield return new ToolCallStreamingStartPart
                    {
                        ToolCallId = toolId,
                        ToolName = currentToolName,
                        ProviderExecuted = true
                    };

                    currentToolId = toolId;
                    currentToolArgs = new StringBuilder();
                }
                else if (item?["type"]?.GetValue<string>() == "function_call")
                {
                    var toolId = item?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
                    currentToolName = item?["name"]?.GetValue<string>() ?? "unknown_tool";

                    yield return new ToolCallStreamingStartPart
                    {
                        ToolCallId = toolId,
                        ToolName = currentToolName,
                        Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == currentToolName)?.Title,
                        ProviderExecuted = false
                    };

                    currentToolId = toolId;
                    currentToolArgs = new StringBuilder();
                }
            }

            else if (type == "response.mcp_call_arguments.delta")
            {
                var argDelta = node?["delta"]?.GetValue<string>();
                var toolId = node?["item_id"]?.GetValue<string>() ?? currentToolId;
                if (!string.IsNullOrEmpty(argDelta) && toolId is not null)
                {
                    currentToolArgs?.Append(argDelta);
                    yield return new ToolCallDeltaPart
                    {
                        ToolCallId = toolId!,
                        InputTextDelta = argDelta
                    };
                }
            }
            else if (type == "response.mcp_call_arguments.done")
            {
                var args = node?["arguments"]?.GetValue<string>() ?? currentToolArgs?.ToString() ?? "{}";
                var toolId = node?["item_id"]?.GetValue<string>() ?? currentToolId;

                if (toolId is not null)
                {
                    yield return new ToolCallPart
                    {
                        ToolCallId = toolId,
                        ToolName = "mcp_call",
                        Input = currentToolName == "python" ? new { code = args }
                            : JsonSerializer.Deserialize<object>(args) ?? new { },
                        ProviderExecuted = true
                    };
                }
            }
            else if (type == "response.function_call_arguments.delta")
            {
                var argDelta = node?["delta"]?.GetValue<string>();
                var toolId = node?["item_id"]?.GetValue<string>() ?? currentToolId;
                if (!string.IsNullOrEmpty(argDelta) && toolId is not null)
                {
                    currentToolArgs?.Append(argDelta);
                    yield return new ToolCallDeltaPart
                    {
                        ToolCallId = toolId!,
                        InputTextDelta = argDelta
                    };
                }
            }
            else if (type == "response.function_call_arguments.done")
            {
                var args = node?["arguments"]?.GetValue<string>() ?? currentToolArgs?.ToString() ?? "{}";
                var toolId = node?["item_id"]?.GetValue<string>() ?? currentToolId;

                if (toolId is not null)
                {
                    yield return new ToolCallPart
                    {
                        ToolCallId = toolId,
                        ToolName = currentToolName ?? string.Empty,
                        Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == currentToolName)?.Title,
                        Input = JsonSerializer.Deserialize<object>(args) ?? new { },
                        ProviderExecuted = false
                    };

                    yield return new ToolApprovalRequestPart
                    {
                        ToolCallId = toolId,
                        ApprovalId = Guid.NewGuid().ToString(),
                    };

                    currentToolId = null;
                    currentToolArgs = null;
                    currentToolName = null;
                }
            }
            else if (type == "response.output_item.done")
            {

                var item = node?["item"];
                if (item?["type"]?.GetValue<string>() == "mcp_call")
                {
                    var toolId = item?["id"]?.GetValue<string>() ?? currentToolId ?? Guid.NewGuid().ToString("n");
                    var outputText = item?["output"]?.GetValue<string>() ?? "";

                    // ✅ By now we already sent ToolCallPart (input-available)
                    yield return new ToolOutputAvailablePart
                    {
                        ToolCallId = toolId,
                        Output = new CallToolResult
                        {
                            IsError = false,
                            Content = [outputText.ToTextContentBlock()]
                        },
                        ProviderExecuted = true
                    };

                    currentToolId = null;
                    currentToolArgs = null;
                    currentToolName = null;
                }
            }
        }

        // --- 5️⃣ Close text stream ---
        if (textStarted && streamId is not null)
            yield return streamId.ToTextEndUIMessageStreamPart();

        // --- 6️⃣ Finish with real token counts ---
        yield return "stop".ToFinishUIPart(
            modelId,
            outputTokens ?? 0,
            inputTokens ?? 0,
            totalTokens ?? (inputTokens + outputTokens) ?? 0,
            chatRequest.Temperature,
            reasoningTokens);

    }
}