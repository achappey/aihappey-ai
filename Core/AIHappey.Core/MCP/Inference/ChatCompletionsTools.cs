using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.Inference;

[McpServerToolType]
public class ChatCompletionsTools
{
    [Description("Execute an AI request using the Chat Completions endpoint. MCP progress notifications contain accumulated text or reasoning for each choice.")]
    [McpServerTool(
        Title = "AI Chat Completions",
        Name = "ai_chat_completions_execute",
        Destructive = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ChatCompletion),
        Idempotent = false,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIChatCompletions_Execute(
        [Description("AI model identifier, including provider prefix.")] string model,
        [Description("Prompt to send to the model.")] string prompt,
        RequestContext<CallToolRequestParams> requestContext,
        IServiceProvider services,
        [Description("Optional system instructions to guide the model response.")] string? instructions = null,
        [Description("Optional maximum number of output tokens.")] int? maxOutputTokens = null,
        CancellationToken ct = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            var (provider, providerModel) = await InferenceMcpHelpers.ResolveAsync(model, prompt, maxOutputTokens, services, ct);
            var messages = new List<ChatMessage>();
            if (!string.IsNullOrWhiteSpace(instructions))
                messages.Add(new ChatMessage { Role = "system", Content = JsonSerializer.SerializeToElement(instructions) });
            messages.Add(new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement(prompt) });

            var request = new ChatCompletionOptions
            {
                Model = providerModel,
                Messages = messages,
                Stream = true,
                Store = false,
                StreamOptions = new StreamOptions { IncludeUsage = true },
                AdditionalProperties = maxOutputTokens is null
                    ? null
                    : new Dictionary<string, JsonElement> { ["max_tokens"] = JsonSerializer.SerializeToElement(maxOutputTokens.Value) }
            };

            var result = new ChatCompletion { Model = providerModel };
            var choices = new Dictionary<int, ChoiceState>();
            var progress = 1;

            await foreach (var part in provider.CompleteChatStreamingAsync(request, ct))
            {
                result.Id = string.IsNullOrWhiteSpace(part.Id) ? result.Id : part.Id;
                result.Created = part.Created == 0 ? result.Created : part.Created;
                result.Model = string.IsNullOrWhiteSpace(part.Model) ? result.Model : part.Model;
                result.Usage = part.Usage ?? result.Usage;

                foreach (var item in part.Choices)
                {
                    var choiceElement = JsonSerializer.SerializeToElement(item);
                    if (!choiceElement.TryGetProperty("index", out var indexElement)) continue;
                    var index = indexElement.GetInt32();
                    if (!choices.TryGetValue(index, out var choice)) choices[index] = choice = new ChoiceState(index);
                    if (choiceElement.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                        choice.FinishReason = finish.GetString();
                    if (!choiceElement.TryGetProperty("delta", out var delta)) continue;

                    var text = GetString(delta, "content");
                    var reasoning = GetString(delta, "reasoning_content") ?? GetString(delta, "reasoning");
                    if (text is not null)
                        await InferenceMcpHelpers.SendProgressAsync(requestContext, progress++, choice.AppendText(text));
                    if (reasoning is not null)
                        await InferenceMcpHelpers.SendProgressAsync(requestContext, progress++, choice.AppendReasoning(reasoning));
                }
            }

            if (string.IsNullOrWhiteSpace(result.Id))
                throw new InvalidOperationException("The Chat Completions stream completed without any completion chunks.");

            result.Choices = choices.OrderBy(x => x.Key).Select(x => x.Value.ToResult()).ToArray();
            return new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web) };
        });

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class ChoiceState(int index)
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _reasoning = new();
        public string? FinishReason { get; set; }
        public string AppendText(string value) { _text.Append(value); return _text.ToString(); }
        public string AppendReasoning(string value) { _reasoning.Append(value); return _reasoning.ToString(); }
        public object ToResult() => new
        {
            index,
            finish_reason = FinishReason,
            message = new
            {
                role = "assistant",
                content = _text.ToString(),
                reasoning_content = _reasoning.Length == 0 ? null : _reasoning.ToString()
            }
        };
    }
}
