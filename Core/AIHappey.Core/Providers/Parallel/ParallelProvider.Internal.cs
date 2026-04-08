using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Parallel;

public partial class ParallelProvider
{
    private const string ChatCompletionsPath = "v1beta/chat/completions";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static List<object> NormalizeCompletionMessages(IEnumerable<ChatMessage>? messages)
    {
        var normalized = new List<object>();

        foreach (var message in messages ?? [])
        {
            var role = NormalizeRole(message.Role);
            var content = FlattenCompletionMessageContent(message.Content);

            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
                content = $"[tool_call_id:{message.ToolCallId}]\n{content}";

            if (message.ToolCalls is not null)
            {
                var toolCallsJson = JsonSerializer.Serialize(message.ToolCalls, Json);
                content = $"{content}\n[tool_calls]{toolCallsJson}";
            }

            normalized.Add(new
            {
                role,
                content = content ?? string.Empty,
                name = (string?)null
            });
        }

        return normalized;
    }

    private static string NormalizeRole(string? role)
    {
        var value = role?.Trim().ToLowerInvariant();
        return value switch
        {
            "system" => "system",
            "assistant" => "assistant",
            _ => "user"
        };
    }

    private static string FlattenCompletionMessageContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        parts.Add(s);
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    parts.Add(item.GetRawText());
                    continue;
                }

                var type = item.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                    ? typeEl.GetString()
                    : null;

                if (type == "text" && item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                    continue;
                }

                if (item.TryGetProperty("text", out var genericTextEl) && genericTextEl.ValueKind == JsonValueKind.String)
                {
                    var text = genericTextEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                    continue;
                }

                parts.Add(item.GetRawText());
            }

            return string.Join("\n", parts.Where(a => !string.IsNullOrWhiteSpace(a)));
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            if (content.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? string.Empty;

            return content.GetRawText();
        }

        return content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? string.Empty
            : content.GetRawText();
    }

    private static string ExtractAssistantTextFromChoices(IEnumerable<object> choices)
    {
        var lines = new List<string>();

        foreach (var choice in choices ?? [])
        {
            JsonElement root;
            try
            {
                root = JsonSerializer.SerializeToElement(choice, Json);
            }
            catch
            {
                continue;
            }

            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                continue;

            if (!message.TryGetProperty("content", out var content))
                continue;

            var text = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Join("\n", content.EnumerateArray()
                    .Select(item =>
                        item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("text", out var textEl)
                        && textEl.ValueKind == JsonValueKind.String
                            ? textEl.GetString()
                            : item.GetRawText())
                    .Where(s => !string.IsNullOrWhiteSpace(s))),
                _ => content.GetRawText()
            };

            if (!string.IsNullOrWhiteSpace(text))
                lines.Add(text);
        }

        return string.Join("\n", lines);
    }

    private ChatCompletionOptions BuildChatOptionsFromChatRequest(ChatRequest chatRequest, bool stream)
    {
        var messages = new List<ChatMessage>();

        foreach (var message in chatRequest.Messages ?? [])
        {
            var role = message.Role switch
            {
                Role.system => "system",
                Role.assistant => "assistant",
                _ => "user"
            };

            var lines = new List<string>();
            foreach (var part in message.Parts ?? [])
            {
                switch (part)
                {
                    case TextUIPart t when !string.IsNullOrWhiteSpace(t.Text):
                        lines.Add(t.Text);
                        break;
                    case ReasoningUIPart reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                        lines.Add(reasoning.Text);
                        break;
                    case ToolInvocationPart tip:
                        lines.Add($"tool:{tip.ToolCallId}:{tip.Title ?? tip.Type}:{JsonSerializer.Serialize(tip.Input, Json)}");
                        if (tip.Output is not null)
                            lines.Add($"tool_output:{tip.ToolCallId}:{JsonSerializer.Serialize(tip.Output, Json)}");
                        break;
                }
            }

            var content = string.Join("\n", lines.Where(a => !string.IsNullOrWhiteSpace(a)));
            if (string.IsNullOrWhiteSpace(content))
                continue;

            messages.Add(new ChatMessage
            {
                Role = role,
                Content = JsonSerializer.SerializeToElement(content, Json)
            });
        }

        if (messages.Count == 0)
        {
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(string.Empty, Json)
            });
        }

        return new ChatCompletionOptions
        {
            Model = chatRequest.Model,
            Temperature = chatRequest.Temperature,
            Stream = stream,
            Messages = messages,
            Tools = [.. (chatRequest.Tools ?? [])
                .Select(t => new
                {
                    type = "function",
                    function = new
                    {
                        name = t.Name,
                        description = t.Description,
                        parameters = (object?)(t.InputSchema is null
                            ? new { type = "object", properties = new { }, required = new string[0] }
                            : t.InputSchema)
                    }
                })
                .Cast<object>()],
            ToolChoice = chatRequest.ToolChoice,
            ResponseFormat = chatRequest.ResponseFormat
        };
    }


    private sealed class ToolBufferState(int index)
    {
        public int Index { get; } = index;
        public string? ToolName { get; set; }
        public StringBuilder Args { get; } = new();
        public bool ProviderExecuted { get; set; }
    }
}

