using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    private async Task<TavilyQueuedTask> QueueResearchTaskAsync(
        string input,
        string model,
        bool stream,
        object? outputSchema,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["input"] = input,
            ["model"] = model,
            ["stream"] = stream
        };

        if (outputSchema is not null)
            payload["output_schema"] = outputSchema;

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var req = new HttpRequestMessage(HttpMethod.Post, "research")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Tavily research queue failed ({(int)resp.StatusCode}): {err}");
        }

        await using var body = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

        var root = doc.RootElement;
        if (!root.TryGetProperty("request_id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Tavily response did not include a request_id.");

        return new TavilyQueuedTask
        {
            RequestId = idEl.GetString()!,
            Status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString() ?? "pending"
                : "pending"
        };
    }

    private async Task<TavilyCompletedTask> WaitForResearchCompletionAsync(string requestId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"research/{requestId}");
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                await Task.Delay(800, cancellationToken);
                continue;
            }

            var text = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Tavily research poll failed ({(int)resp.StatusCode}): {text}");

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString()
                : null;

            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return ToCompletedTask(root);

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Tavily research task '{requestId}' failed.");

            await Task.Delay(800, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async IAsyncEnumerable<TavilyStreamEvent> StreamResearchEventsAsync(
        string input,
        string model,
        object? outputSchema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["input"] = input,
            ["model"] = model,
            ["stream"] = true
        };

        if (outputSchema is not null)
            payload["output_schema"] = outputSchema;

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var req = new HttpRequestMessage(HttpMethod.Post, "research")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Tavily streaming research failed ({(int)resp.StatusCode}): {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                yield break;

            if (line.Length == 0 || line.StartsWith(':'))
                continue;

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                var evtName = line["event:".Length..].Trim();
                if (evtName.Equals("done", StringComparison.OrdinalIgnoreCase))
                    yield break;

                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0 || data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                yield break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (root.TryGetProperty("object", out var objectEl)
                && objectEl.ValueKind == JsonValueKind.String
                && string.Equals(objectEl.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                var err = root.TryGetProperty("error", out var errEl)
                    ? errEl.GetString() ?? "Unknown Tavily streaming error"
                    : "Unknown Tavily streaming error";
                throw new InvalidOperationException(err);
            }

            var evt = ToStreamEvent(root);
            if (evt is not null)
                yield return evt;
        }
    }

    private static TavilyStreamEvent? ToStreamEvent(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;

        var model = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString()
            : null;

        var created = root.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number
            ? createdEl.GetInt64()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != JsonValueKind.Array)
            return null;

        var firstChoice = choicesEl.EnumerateArray().FirstOrDefault();
        if (firstChoice.ValueKind != JsonValueKind.Object)
            return null;

        var finishReason = firstChoice.TryGetProperty("finish_reason", out var finishEl) && finishEl.ValueKind == JsonValueKind.String
            ? finishEl.GetString()
            : null;

        if (!firstChoice.TryGetProperty("delta", out var deltaEl) || deltaEl.ValueKind != JsonValueKind.Object)
        {
            return new TavilyStreamEvent
            {
                Id = id,
                Model = model,
                Created = created,
                FinishReason = finishReason,
                Usage = root.TryGetProperty("usage", out var usageOnly) && usageOnly.ValueKind == JsonValueKind.Object
                    ? usageOnly.Clone()
                    : null
            };
        }

        string? contentText = null;
        JsonElement? contentObject = null;
        if (deltaEl.TryGetProperty("content", out var contentEl))
        {
            if (contentEl.ValueKind == JsonValueKind.String)
                contentText = contentEl.GetString();
            else if (contentEl.ValueKind == JsonValueKind.Object || contentEl.ValueKind == JsonValueKind.Array)
                contentObject = contentEl.Clone();
        }

        var sources = new List<TavilySource>();
        if (deltaEl.TryGetProperty("sources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
            sources.AddRange(ParseSources(sourcesEl));

        if (deltaEl.TryGetProperty("tool_calls", out var toolCallsEl)
            && toolCallsEl.ValueKind == JsonValueKind.Object
            && toolCallsEl.TryGetProperty("tool_response", out var toolResponsesEl)
            && toolResponsesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var response in toolResponsesEl.EnumerateArray())
            {
                if (response.ValueKind != JsonValueKind.Object)
                    continue;

                if (response.TryGetProperty("sources", out var nestedSources) && nestedSources.ValueKind == JsonValueKind.Array)
                    sources.AddRange(ParseSources(nestedSources));
            }
        }

        return new TavilyStreamEvent
        {
            Id = id,
            Model = model,
            Created = created,
            ContentText = contentText,
            ContentObject = contentObject,
            Sources = sources,
            FinishReason = finishReason,
            Delta = deltaEl.Clone(),
            Usage = root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object
                ? usageEl.Clone()
                : null
        };
    }

   
    
    private static object ToSourceDto(TavilySource source)
        => new
        {
            url = source.Url,
            title = source.Title,
            favicon = source.Favicon
        };

    private static TavilyCompletedTask ToCompletedTask(JsonElement root)
    {
        var requestId = root.TryGetProperty("request_id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
            : Guid.NewGuid().ToString("n");

        var createdAt = root.TryGetProperty("created_at", out var createdAtEl) && createdAtEl.ValueKind == JsonValueKind.String
            ? ParseUtcOrNow(createdAtEl.GetString())
            : DateTime.UtcNow;

        var responseTime = root.TryGetProperty("response_time", out var responseTimeEl) && responseTimeEl.ValueKind == JsonValueKind.Number
            ? responseTimeEl.GetDouble()
            : 0;

        object content = string.Empty;
        if (root.TryGetProperty("content", out var contentEl))
        {
            content = contentEl.ValueKind == JsonValueKind.String
                ? (object)(contentEl.GetString() ?? string.Empty)
                : JsonSerializer.Deserialize<object>(contentEl.GetRawText(), JsonSerializerOptions.Web) ?? string.Empty;
        }

        var sources = root.TryGetProperty("sources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array
            ? ParseSources(sourcesEl)
            : [];

        return new TavilyCompletedTask
        {
            RequestId = requestId,
            CreatedAt = createdAt,
            Content = content,
            Sources = sources,
            ResponseTime = responseTime
        };
    }

    private static List<TavilySource> ParseSources(JsonElement sourcesElement)
    {
        var list = new List<TavilySource>();

        foreach (var sourceEl in sourcesElement.EnumerateArray())
        {
            if (sourceEl.ValueKind != JsonValueKind.Object)
                continue;

            var url = sourceEl.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(url))
                continue;

            var title = sourceEl.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                ? titleEl.GetString()
                : null;

            var favicon = sourceEl.TryGetProperty("favicon", out var faviconEl) && faviconEl.ValueKind == JsonValueKind.String
                ? faviconEl.GetString()
                : null;

            list.Add(new TavilySource
            {
                Url = url!,
                Title = title,
                Favicon = favicon
            });
        }

        return list;
    }
  
    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage> messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        var system = new List<string>();

        foreach (var msg in all)
        {
            var role = (msg.Role ?? string.Empty).Trim().ToLowerInvariant();
            var text = ExtractCompletionMessageText(msg.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (role == "system")
            {
                system.Add(text!);
                continue;
            }

            if (role is not ("user" or "assistant"))
                continue;

            lines.Add($"{role}: {text}");
        }

        if (system.Count > 0)
            lines.Insert(0, $"system: {string.Join("\n\n", system)}");

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromUiMessages(IEnumerable<UIMessage> messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        foreach (var msg in all)
        {
            var text = string.Join("\n", msg.Parts
                .OfType<TextUIPart>()
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));

            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{msg.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromResponseRequest(ResponseRequest request)
    {
        var prompt = BuildPromptFromResponseInput(request.Input);
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = request.Instructions ?? string.Empty;

        return prompt;
    }

    private static string BuildPromptFromResponseInput(ResponseInput? input)
    {
        if (input is null)
            return string.Empty;

        if (input.IsText)
            return input.Text ?? string.Empty;

        if (input.IsItems != true || input.Items is null)
            return string.Empty;

        var lines = new List<string>();
        foreach (var item in input.Items)
        {
            if (item is not ResponseInputMessage message)
                continue;

            var role = message.Role.ToString().ToLowerInvariant();
            var text = message.Content.IsText
                ? message.Content.Text
                : string.Join("\n", message.Content.Parts?.OfType<InputTextPart>().Select(p => p.Text) ?? []);

            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string? ExtractCompletionMessageText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    builder.Append(part.GetString());
                    continue;
                }

                if (part.ValueKind != JsonValueKind.Object)
                    continue;

                if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    builder.Append(textEl.GetString());
                else if (part.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                    builder.Append(contentEl.GetString());
            }

            var value = builder.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        if (content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("text", out var objectText)
            && objectText.ValueKind == JsonValueKind.String)
        {
            return objectText.GetString();
        }

        return content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : content.GetRawText();
    }

    private static object? TryExtractOutputSchema(object? format)
    {
        if (format is null)
            return null;

        var schema = format.GetJSONSchema();
        if (schema?.JsonSchema is not null)
        {
            var element = schema.JsonSchema.Schema;
            if (element.ValueKind != JsonValueKind.Undefined && element.ValueKind != JsonValueKind.Null)
            {
                return JsonSerializer.Deserialize<object>(element.GetRawText(), JsonSerializerOptions.Web);
            }
        }

        try
        {
            var raw = JsonSerializer.SerializeToElement(format, JsonSerializerOptions.Web);

            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("json_schema", out var jsonSchema)
                && jsonSchema.ValueKind == JsonValueKind.Object
                && jsonSchema.TryGetProperty("schema", out var schemaEl)
                && schemaEl.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<object>(schemaEl.GetRawText(), JsonSerializerOptions.Web);
            }

            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("schema", out var directSchema)
                && directSchema.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<object>(directSchema.GetRawText(), JsonSerializerOptions.Web);
            }
        }
        catch
        {
            // ignore schema extraction failures
        }

        return null;
    }

    private static string ToOutputText(object? content)
    {
        if (content is null)
            return string.Empty;

        if (content is string text)
            return text;

        return JsonSerializer.Serialize(content, JsonSerializerOptions.Web);
    }

    private static Dictionary<string, object?>? BuildSourcesMetadata(IEnumerable<TavilySource> sources)
    {
        var sourceList = sources?.ToList() ?? [];
        if (sourceList.Count == 0)
            return null;

        return new Dictionary<string, object?>
        {
            ["sources"] = sourceList.Select(ToSourceDto).ToList()
        };
    }

    private static Dictionary<string, object?>? MergeMetadata(
        Dictionary<string, object?>? existing,
        Dictionary<string, object?>? add)
    {
        if (existing is null && add is null)
            return null;

        var merged = existing is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(existing);

        if (add is not null)
        {
            foreach (var kvp in add)
                merged[kvp.Key] = kvp.Value;
        }

        return merged;
    }

    private static DateTime ParseUtcOrNow(string? value)
        => DateTime.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.UtcNow;

    private static long ToUnixTime(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private sealed class TavilyQueuedTask
    {
        public string RequestId { get; init; } = default!;

        public string Status { get; init; } = default!;
    }

    private sealed class TavilyCompletedTask
    {
        public string RequestId { get; init; } = default!;

        public DateTime CreatedAt { get; init; }

        public object? Content { get; init; }

        public List<TavilySource> Sources { get; init; } = [];

        public double ResponseTime { get; init; }
    }

    private sealed class TavilyStreamEvent
    {
        public string? Id { get; init; }

        public string? Model { get; init; }

        public long Created { get; init; }

        public string? ContentText { get; init; }

        public JsonElement? ContentObject { get; init; }

        public List<TavilySource> Sources { get; init; } = [];

        public string? FinishReason { get; init; }

        public JsonElement? Delta { get; init; }

        public JsonElement? Usage { get; init; }
    }

    private sealed class TavilySource
    {
        public string Url { get; init; } = default!;

        public string? Title { get; init; }

        public string? Favicon { get; init; }
    }
}

