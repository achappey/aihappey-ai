using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.TeamDay;

public partial class TeamDayProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private sealed class TeamDayRequestMetadata
    {
        [JsonPropertyName("spaceId")]
        public string? SpaceId { get; set; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("chatId")]
        public string? ChatId { get; set; }
    }

    private sealed class TeamDayExecutionResult
    {
        public string ExecutionId { get; init; } = Guid.NewGuid().ToString("n");
        public string? ChatId { get; init; }
        public string? SessionId { get; init; }
        public string Text { get; init; } = string.Empty;
        public object? Usage { get; init; }
    }

    private abstract class TeamDayStreamEvent;

    private sealed class TeamDayMetaStreamEvent : TeamDayStreamEvent
    {
        public string? ExecutionId { get; init; }
        public string? ChatId { get; init; }
        public string? SessionId { get; init; }
    }

    private sealed class TeamDayDeltaStreamEvent : TeamDayStreamEvent
    {
        public string Text { get; init; } = string.Empty;
    }

    private sealed class TeamDayResultStreamEvent : TeamDayStreamEvent
    {
        public string? SessionId { get; init; }
        public object? Usage { get; init; }
    }

    private sealed class TeamDayErrorStreamEvent : TeamDayStreamEvent
    {
        public string Message { get; init; } = "TeamDay stream failed.";
    }






    private async Task<TeamDayExecutionResult> ExecuteAgentAsync(
        string agentId,
        string message,
        TeamDayRequestMetadata? metadata,
        bool stream,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var payload = BuildExecutePayload(message, metadata, stream);
        var json = JsonSerializer.Serialize(payload, Json);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"agents/{Uri.EscapeDataString(agentId)}/execute")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"TeamDay execute error ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException($"TeamDay execute error: {TryGetString(root, "message") ?? body}");

        return new TeamDayExecutionResult
        {
            ExecutionId = TryGetString(root, "executionId") ?? Guid.NewGuid().ToString("n"),
            ChatId = TryGetString(root, "chatId"),
            SessionId = TryGetString(root, "sessionId"),
            Text = TryGetString(root, "result") ?? string.Empty,
            Usage = TryGetUsageObject(root)
        };
    }

    private async IAsyncEnumerable<TeamDayStreamEvent> StreamAgentExecutionAsync(
        string agentId,
        string message,
        TeamDayRequestMetadata? metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var payload = BuildExecutePayload(message, metadata, stream: true);
        var json = JsonSerializer.Serialize(payload, Json);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"agents/{Uri.EscapeDataString(agentId)}/execute")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"TeamDay stream error ({(int)resp.StatusCode}): {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? eventType = null;
        var dataLines = new List<string>();

        static string? Flush(List<string> lines)
        {
            if (lines.Count == 0)
                return null;

            var payloadData = string.Join("\n", lines);
            lines.Clear();
            return string.IsNullOrWhiteSpace(payloadData) ? null : payloadData.Trim();
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                var payloadData = Flush(dataLines);
                if (payloadData is not null)
                {
                    foreach (var evt in ParseSseEvent(eventType, payloadData))
                        yield return evt;
                }

                eventType = null;
                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventType = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line["data:".Length..].TrimStart());
        }

        var trailing = Flush(dataLines);
        if (trailing is not null)
        {
            foreach (var evt in ParseSseEvent(eventType, trailing))
                yield return evt;
        }
    }

    private static IEnumerable<TeamDayStreamEvent> ParseSseEvent(string? eventType, string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload is "[DONE]" or "[done]")
            yield break;

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            switch ((eventType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "meta":
                    yield return new TeamDayMetaStreamEvent
                    {
                        ExecutionId = TryGetString(root, "executionId"),
                        ChatId = TryGetString(root, "chatId"),
                        SessionId = TryGetString(root, "sessionId")
                    };
                    yield break;

                case "result":
                    yield return new TeamDayResultStreamEvent
                    {
                        SessionId = TryGetString(root, "sessionId"),
                        Usage = TryGetUsageObject(root)
                    };
                    yield break;

                case "error":
                    yield return new TeamDayErrorStreamEvent
                    {
                        Message = TryGetString(root, "message") ?? "TeamDay stream failed."
                    };
                    yield break;

                case "message":
                    var text = ExtractStreamText(root);
                    if (!string.IsNullOrEmpty(text))
                        yield return new TeamDayDeltaStreamEvent { Text = text };
                    yield break;
            }

            var fallbackText = ExtractStreamText(root);
            if (!string.IsNullOrEmpty(fallbackText))
                yield return new TeamDayDeltaStreamEvent { Text = fallbackText };
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private static Dictionary<string, object?> BuildExecutePayload(string message, TeamDayRequestMetadata? metadata, bool stream)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["message"] = message,
            ["stream"] = stream
        };

        if (!string.IsNullOrWhiteSpace(metadata?.SpaceId))
            payload["spaceId"] = metadata.SpaceId;

        if (!string.IsNullOrWhiteSpace(metadata?.SessionId))
            payload["sessionId"] = metadata.SessionId;

        if (!string.IsNullOrWhiteSpace(metadata?.ChatId))
            payload["chatId"] = metadata.ChatId;

        return payload;
    }

    private static string NormalizeAgentModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var trimmed = model.Trim();
        return trimmed.Contains('/', StringComparison.Ordinal)
            ? trimmed.SplitModelId().Model
            : trimmed;
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
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        parts.Add(value);
                    continue;
                }

                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    var value = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        parts.Add(value);
                    continue;
                }

                parts.Add(item.GetRawText());
            }

            return string.Join("\n", parts.Where(static p => !string.IsNullOrWhiteSpace(p)));
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            if (content.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                return textEl.GetString() ?? string.Empty;

            return content.GetRawText();
        }

        return content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? string.Empty
            : content.GetRawText();
    }

    private static string ApplyStructuredOutputInstructions(string prompt, object? responseFormat)
    {
        var schema = ExtractSchemaText(responseFormat);
        if (string.IsNullOrWhiteSpace(schema))
            return prompt;

        return string.IsNullOrWhiteSpace(prompt)
            ? $"Return JSON that matches this schema exactly:\n{schema}"
            : $"{prompt}\n\nReturn JSON that matches this schema exactly:\n{schema}";
    }

    private static string? ExtractSchemaText(object? responseFormat)
    {
        if (responseFormat is null)
            return null;

        var schema = responseFormat.GetJSONSchema();
        if (schema?.JsonSchema is not null)
        {
            var element = schema.JsonSchema.Schema;
            if (element.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return element.GetRawText();
        }

        try
        {
            return JsonSerializer.Serialize(responseFormat, Json);
        }
        catch
        {
            return null;
        }
    }


    private static string NormalizePotentialJson(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0)
            return trimmed;

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstBreak)
            return trimmed[(firstBreak + 1)..].Trim();

        return trimmed[(firstBreak + 1)..lastFence].Trim();
    }

    private static Dictionary<string, object?> MergeExecutionMetadata(Dictionary<string, object?>? metadata, TeamDayExecutionResult result)
        => MergeExecutionMetadata(metadata, result.ExecutionId, result.ChatId, result.SessionId);

    private static Dictionary<string, object?> MergeExecutionMetadata(
        Dictionary<string, object?>? metadata,
        string executionId,
        string? chatId,
        string? sessionId)
    {
        var merged = metadata is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase);

        merged["teamday_execution_id"] = executionId;

        if (!string.IsNullOrWhiteSpace(chatId))
            merged["teamday_chat_id"] = chatId;

        if (!string.IsNullOrWhiteSpace(sessionId))
            merged["teamday_session_id"] = sessionId;

        return merged;
    }


    private static TeamDayRequestMetadata? TryDeserializeMetadata(object? raw)
    {
        if (raw is null)
            return null;

        try
        {
            return raw switch
            {
                JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Object => jsonElement.Deserialize<TeamDayRequestMetadata>(Json),
                JsonObject jsonObject => jsonObject.Deserialize<TeamDayRequestMetadata>(Json),
                _ => JsonSerializer.Deserialize<TeamDayRequestMetadata>(JsonSerializer.Serialize(raw, Json), Json)
            };
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?>? TryGetUsageObject(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
            return null;

        return BuildUsage(usageEl);
    }

    private static Dictionary<string, object?> BuildUsage(JsonElement usageEl)
    {
        var inputTokens = TryGetInt32(usageEl, "input_tokens");
        var outputTokens = TryGetInt32(usageEl, "output_tokens");
        var totalTokens = TryGetInt32(usageEl, "total_tokens")
            ?? ((inputTokens ?? 0) + (outputTokens ?? 0));

        return new Dictionary<string, object?>
        {
            ["prompt_tokens"] = inputTokens,
            ["completion_tokens"] = outputTokens,
            ["total_tokens"] = totalTokens,
            ["input_tokens"] = inputTokens,
            ["output_tokens"] = outputTokens
        };
    }

    private static string ExtractStreamText(JsonElement root)
    {
        if (root.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.Object)
        {
            if (deltaEl.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String
                && string.Equals(typeEl.GetString(), "text_delta", StringComparison.OrdinalIgnoreCase)
                && deltaEl.TryGetProperty("text", out var textEl)
                && textEl.ValueKind == JsonValueKind.String)
            {
                return textEl.GetString() ?? string.Empty;
            }

            if (deltaEl.TryGetProperty("text", out var fallbackTextEl) && fallbackTextEl.ValueKind == JsonValueKind.String)
                return fallbackTextEl.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("content", out var contentEl))
            return FlattenStreamContent(contentEl);

        if (root.TryGetProperty("text", out var textNode) && textNode.ValueKind == JsonValueKind.String)
            return textNode.GetString() ?? string.Empty;

        return string.Empty;
    }

    private static string FlattenStreamContent(JsonElement contentEl)
    {
        if (contentEl.ValueKind == JsonValueKind.String)
            return contentEl.GetString() ?? string.Empty;

        if (contentEl.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = new List<string>();
        foreach (var item in contentEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    parts.Add(value);
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                {
                    var value = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        parts.Add(value);
                    continue;
                }

                if (item.TryGetProperty("content", out var nestedContent))
                {
                    var nested = FlattenStreamContent(nestedContent);
                    if (!string.IsNullOrWhiteSpace(nested))
                        parts.Add(nested);
                }
            }
        }

        return string.Join("\n", parts.Where(static p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;

        return prop.GetString();
    }

    private static int? TryGetUsageInt(object? usage, string propertyName)
    {
        if (usage is null)
            return null;

        try
        {
            var element = JsonSerializer.SerializeToElement(usage, Json);
            return TryGetInt32(element, propertyName);
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value))
            return value;

        if (prop.ValueKind == JsonValueKind.String
            && int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        return null;
    }

    private static string SerializeCompact(object? value)
    {
        if (value is null)
            return "null";

        try
        {
            return JsonSerializer.Serialize(value, Json);
        }
        catch
        {
            return value.ToString() ?? "null";
        }
    }
}
