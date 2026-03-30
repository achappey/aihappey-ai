using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MIMICXAI;

public partial class MIMICXAIProvider
{
    private const string NativeTextFamily = "darwin";
    private const string NativeTextTask = "conversation";

   

    private async Task<MimicXAiGeneratedText> ExecuteNativeGenerateAsync(
        MimicXAiNativeTextRequest request,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/generate")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"MIMICXAI generate error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        return new MimicXAiGeneratedText
        {
            Id = Guid.NewGuid().ToString("n"),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Text = ExtractGeneratedText(raw)
        };
    }

    private async IAsyncEnumerable<string> ExecuteNativeStreamTextAsync(
        MimicXAiNativeTextRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/stream")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"MIMICXAI stream error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.StartsWith(':'))
                continue;

            if (line.Length == 0)
            {
                if (dataLines.Count == 0)
                    continue;

                var payload = string.Join("\n", dataLines);
                dataLines.Clear();

                var parsed = ParseStreamPayload(payload);
                foreach (var delta in parsed.Deltas)
                    yield return delta;

                if (parsed.IsDone)
                    yield break;

                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line["data:".Length..].TrimStart());
                continue;
            }

            dataLines.Add(line);
        }

        if (dataLines.Count == 0)
            yield break;

        var trailingPayload = string.Join("\n", dataLines);
        var trailingParsed = ParseStreamPayload(trailingPayload);
        foreach (var delta in trailingParsed.Deltas)
            yield return delta;
    }

    private static MimicXAiNativeTextRequest BuildNativeTextRequest(ChatRequest chatRequest)
    {
        var (prompt, systemPrompt) = BuildPromptFromUiMessages(chatRequest.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("MIMICXAI requires at least one text message.", nameof(chatRequest));

        // MimicXAI native text endpoints document prompt/system_prompt text generation only.
        // Incoming tools and response_format are intentionally ignored for this provider.
        return new MimicXAiNativeTextRequest
        {
            Family = NativeTextFamily,
            Task = NativeTextTask,
            Prompt = prompt,
            SystemPrompt = systemPrompt,
            MaxTokens = chatRequest.MaxOutputTokens
        };
    }

    private static MimicXAiNativeTextRequest BuildNativeTextRequest(ChatCompletionOptions options)
    {
        var (prompt, systemPrompt) = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("MIMICXAI requires non-empty chat input.", nameof(options));

        // MimicXAI native text endpoints document prompt/system_prompt text generation only.
        // Incoming tools and response_format are intentionally ignored for this provider.
        return new MimicXAiNativeTextRequest
        {
            Family = NativeTextFamily,
            Task = NativeTextTask,
            Prompt = prompt,
            SystemPrompt = systemPrompt
        };
    }

    private static MimicXAiNativeTextRequest BuildNativeTextRequest(ResponseRequest options)
    {
        var (prompt, systemPrompt) = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("MIMICXAI responses require non-empty input.", nameof(options));

        // MimicXAI native text endpoints document prompt/system_prompt text generation only.
        // Incoming tools and structured text settings are intentionally ignored for this provider.
        return new MimicXAiNativeTextRequest
        {
            Family = NativeTextFamily,
            Task = NativeTextTask,
            Prompt = prompt,
            SystemPrompt = systemPrompt,
            MaxTokens = options.MaxOutputTokens
        };
    }

    private static (string Prompt, string? SystemPrompt) BuildPromptFromUiMessages(IEnumerable<UIMessage>? messages)
    {
        var promptMessages = new List<(string Role, string Text)>();
        string? systemPrompt = null;

        foreach (var message in messages ?? [])
        {
            var text = ExtractText(message.Parts);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (message.Role == Role.system)
            {
                systemPrompt = AppendParagraph(systemPrompt, text);
                continue;
            }

            promptMessages.Add((NormalizeRole(message.Role), text));
        }

        return (BuildPromptTranscript(promptMessages), systemPrompt);
    }

    private static (string Prompt, string? SystemPrompt) BuildPromptFromCompletionMessages(IEnumerable<ChatMessage>? messages)
    {
        var promptMessages = new List<(string Role, string Text)>();
        string? systemPrompt = null;

        foreach (var message in messages ?? [])
        {
            var text = ExtractText(message.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                systemPrompt = AppendParagraph(systemPrompt, text);
                continue;
            }

            promptMessages.Add((NormalizeRole(message.Role), text));
        }

        return (BuildPromptTranscript(promptMessages), systemPrompt);
    }

    private static (string Prompt, string? SystemPrompt) BuildPromptFromResponseRequest(ResponseRequest options)
    {
        var promptMessages = new List<(string Role, string Text)>();
        var systemPrompt = AppendParagraph(null, options.Instructions);

        if (options.Input is null)
            return (string.Empty, systemPrompt);

        if (options.Input.IsText)
            return (options.Input.Text ?? string.Empty, systemPrompt);

        foreach (var item in options.Input.Items ?? [])
        {
            if (item is not ResponseInputMessage message)
                continue;

            var text = ExtractText(message.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (message.Role is ResponseRole.System or ResponseRole.Developer)
            {
                systemPrompt = AppendParagraph(systemPrompt, text);
                continue;
            }

            promptMessages.Add((NormalizeRole(message.Role), text));
        }

        return (BuildPromptTranscript(promptMessages), systemPrompt);
    }

    private static string BuildPromptTranscript(IReadOnlyList<(string Role, string Text)> messages)
    {
        if (messages.Count == 0)
            return string.Empty;

        if (messages.Count == 1 && string.Equals(messages[0].Role, "user", StringComparison.OrdinalIgnoreCase))
            return messages[0].Text;

        return string.Join("\n\n", messages.Select(message => $"{message.Role}: {message.Text}"));
    }

    private static string? ExtractText(IEnumerable<UIMessagePart>? parts)
    {
        var segments = new List<string>();

        foreach (var part in parts ?? [])
        {
            switch (part)
            {
                case TextUIPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                    segments.Add(textPart.Text);
                    break;
                case ReasoningUIPart reasoningPart when !string.IsNullOrWhiteSpace(reasoningPart.Text):
                    segments.Add(reasoningPart.Text);
                    break;
            }
        }

        return segments.Count == 0 ? null : string.Join("\n", segments);
    }

    private static string ExtractText(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? string.Empty;
            case JsonValueKind.Array:
                return string.Join(
                    "\n",
                    content.EnumerateArray()
                        .Select(ExtractText)
                        .Where(text => !string.IsNullOrWhiteSpace(text)));
            case JsonValueKind.Object:
                if (content.TryGetProperty("text", out var textProperty))
                    return ExtractText(textProperty);

                if (content.TryGetProperty("content", out var contentProperty))
                    return ExtractText(contentProperty);

                return string.Empty;
            default:
                return string.Empty;
        }
    }

    private static string ExtractText(ResponseMessageContent content)
    {
        if (content.IsText)
            return content.Text ?? string.Empty;

        return ExtractText(content.Parts);
    }

    private static string ExtractText(IEnumerable<ResponseContentPart>? parts)
    {
        var segments = new List<string>();

        foreach (var part in parts ?? [])
        {
            if (part is InputTextPart textPart && !string.IsNullOrWhiteSpace(textPart.Text))
                segments.Add(textPart.Text);
        }

        return string.Join("\n", segments);
    }

    private static string NormalizeRole(Role role)
        => role switch
        {
            Role.assistant => "assistant",
            Role.system => "system",
            _ => "user"
        };

    private static string NormalizeRole(ResponseRole role)
        => role switch
        {
            ResponseRole.Assistant => "assistant",
            ResponseRole.System => "system",
            ResponseRole.Developer => "developer",
            _ => "user"
        };

    private static string NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return "user";

        return role.Trim().ToLowerInvariant() switch
        {
            "assistant" => "assistant",
            "system" => "system",
            "developer" => "developer",
            _ => "user"
        };
    }

    private static string ExtractGeneratedText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return ExtractGeneratedText(doc.RootElement);
        }
        catch (JsonException)
        {
            return raw.Trim();
        }
    }

    private static string ExtractGeneratedText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;

            case JsonValueKind.Array:
                return string.Join(
                    "\n",
                    element.EnumerateArray()
                        .Select(ExtractGeneratedText)
                        .Where(text => !string.IsNullOrWhiteSpace(text)));

            case JsonValueKind.Object:
                if (TryGetErrorMessage(element, out var errorMessage))
                    throw new InvalidOperationException(errorMessage);

                if (element.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                {
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("message", out var message))
                        {
                            var content = ExtractGeneratedText(message);
                            if (!string.IsNullOrWhiteSpace(content))
                                return content;
                        }

                        if (choice.TryGetProperty("text", out var choiceText))
                        {
                            var content = ExtractGeneratedText(choiceText);
                            if (!string.IsNullOrWhiteSpace(content))
                                return content;
                        }
                    }
                }

                foreach (var propertyName in new[] { "text", "content", "output_text", "response", "result", "data" })
                {
                    if (!element.TryGetProperty(propertyName, out var propertyValue))
                        continue;

                    var content = ExtractGeneratedText(propertyValue);
                    if (!string.IsNullOrWhiteSpace(content))
                        return content;
                }

                return string.Empty;

            default:
                return string.Empty;
        }
    }

    private static MimicXAiStreamPayload ParseStreamPayload(string payload)
    {
        var result = new MimicXAiStreamPayload();
        if (string.IsNullOrWhiteSpace(payload))
            return result;

        var trimmed = payload.Trim();
        if (trimmed is "[DONE]" or "[done]")
        {
            result.IsDone = true;
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);

            var isDone = result.IsDone;
            CollectStreamPayload(doc.RootElement, result.Deltas, ref isDone);
            result.IsDone = isDone;

            return result;
        }
        catch (JsonException)
        {
            result.Deltas.Add(trimmed);
            return result;
        }
    }

    private static void CollectStreamPayload(JsonElement element, List<string> deltas, ref bool isDone)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    deltas.Add(text);
                return;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectStreamPayload(item, deltas, ref isDone);
                return;

            case JsonValueKind.Object:
                if (TryGetErrorMessage(element, out var errorMessage))
                    throw new InvalidOperationException(errorMessage);

                if (element.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                    isDone = true;

                if (element.TryGetProperty("is_done", out var isDoneElement) && isDoneElement.ValueKind == JsonValueKind.True)
                    isDone = true;

                if (element.TryGetProperty("finish_reason", out var finishReason)
                    && finishReason.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(finishReason.GetString()))
                    isDone = true;

                if (element.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && IsTerminalEvent(type.GetString()))
                    isDone = true;

                if (element.TryGetProperty("event", out var @event)
                    && @event.ValueKind == JsonValueKind.String
                    && IsTerminalEvent(@event.GetString()))
                    isDone = true;

                if (element.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                {
                    foreach (var choice in choices.EnumerateArray())
                        CollectStreamPayload(choice, deltas, ref isDone);
                }

                foreach (var propertyName in new[] { "delta", "text", "token", "content", "output_text", "message", "response", "result", "data" })
                {
                    if (!element.TryGetProperty(propertyName, out var propertyValue))
                        continue;

                    CollectStreamPayload(propertyValue, deltas, ref isDone);
                }

                return;
        }
    }

    private static bool TryGetErrorMessage(JsonElement element, out string message)
    {
        message = string.Empty;

        if (!element.TryGetProperty("error", out var errorElement))
            return false;

        message = errorElement.ValueKind switch
        {
            JsonValueKind.String => errorElement.GetString() ?? "MIMICXAI request failed.",
            JsonValueKind.Object when errorElement.TryGetProperty("message", out var messageElement) => messageElement.GetString() ?? "MIMICXAI request failed.",
            _ => "MIMICXAI request failed."
        };

        return true;
    }

    private static bool IsTerminalEvent(string? eventName)
        => eventName?.Trim().ToLowerInvariant() is "done" or "complete" or "completed" or "finish" or "finished";

    private static ResponseResult CreateStreamingResponseState(string responseId, ResponseRequest options, long createdAt)
        => new()
        {
            Id = responseId,
            Model = options.Model ?? string.Empty,
            CreatedAt = createdAt,
            Status = "in_progress",
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Output = []
        };

    private static IEnumerable<object> BuildResponseOutput(string itemId, string text)
    {
        return
        [
            new
            {
                type = "message",
                id = itemId,
                status = "completed",
                role = "assistant",
                content = new[]
                {
                    new
                    {
                        type = "output_text",
                        text,
                        annotations = Array.Empty<string>()
                    }
                }
            }
        ];
    }

    private static string? AppendParagraph(string? current, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return current;

        if (string.IsNullOrWhiteSpace(current))
            return value.Trim();

        return $"{current}\n\n{value.Trim()}";
    }

    private sealed class MimicXAiNativeTextRequest
    {
        [JsonPropertyName("family")]
        public string Family { get; init; } = default!;

        [JsonPropertyName("task")]
        public string Task { get; init; } = default!;

        [JsonPropertyName("prompt")]
        public string Prompt { get; init; } = default!;

        [JsonPropertyName("max_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; init; }

        [JsonPropertyName("system_prompt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SystemPrompt { get; init; }

        [JsonPropertyName("kwargs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object?>? Kwargs { get; init; }
    }

    private sealed class MimicXAiGeneratedText
    {
        public string Id { get; init; } = default!;

        public long CreatedAt { get; init; }

        public string Text { get; init; } = string.Empty;
    }

    private sealed class MimicXAiStreamPayload
    {
        public List<string> Deltas { get; } = [];

        public bool IsDone { get; set; }
    }
}
