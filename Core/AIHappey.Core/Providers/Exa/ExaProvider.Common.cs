using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.Exa;

public partial class ExaProvider
{
    private const string AnswerModelId = "exa";
    private const string ResearchModelId = "exa-research";
    private const string ResearchProModelId = "exa-research-pro";
    private const string ResearchFastModelId = "exa-research-fast";

    private static readonly JsonSerializerOptions JsonWeb = JsonSerializerOptions.Web;

    private static bool IsAnswerModel(string? model)
        => string.Equals(model, AnswerModelId, StringComparison.OrdinalIgnoreCase);

    private static bool IsResearchFastModel(string? model)
        => string.Equals(model, ResearchFastModelId, StringComparison.OrdinalIgnoreCase);

    private static bool IsResearchModel(string? model)
        => string.Equals(model, ResearchModelId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, ResearchProModelId, StringComparison.OrdinalIgnoreCase);

    private void ApplyChatAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Exa)} API key.");

        _client.DefaultRequestHeaders.Remove("x-api-key");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private void ApplyResearchAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Exa)} API key.");

        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Remove("x-api-key");
        _client.DefaultRequestHeaders.Add("x-api-key", key);
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

    private static string BuildPromptFromUiMessages(IEnumerable<AIHappey.Vercel.Models.UIMessage> messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        foreach (var msg in all)
        {
            var text = string.Join("\n", msg.Parts
                .OfType<AIHappey.Vercel.Models.TextUIPart>()
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

    private static string BuildPromptFromSamplingMessages(IEnumerable<ModelContextProtocol.Protocol.SamplingMessage> messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        foreach (var msg in all)
        {
            var role = msg.Role switch
            {
                ModelContextProtocol.Protocol.Role.Assistant => "assistant",
                ModelContextProtocol.Protocol.Role.User => "user",
                _ => "user"
            };

            var text = msg.ToText() ?? string.Empty;
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
                return JsonSerializer.Deserialize<object>(element.GetRawText(), JsonWeb);
            }
        }

        try
        {
            var raw = JsonSerializer.SerializeToElement(format, JsonWeb);

            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("json_schema", out var jsonSchema)
                && jsonSchema.ValueKind == JsonValueKind.Object
                && jsonSchema.TryGetProperty("schema", out var schemaEl)
                && schemaEl.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<object>(schemaEl.GetRawText(), JsonWeb);
            }

            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("schema", out var directSchema)
                && directSchema.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<object>(directSchema.GetRawText(), JsonWeb);
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

        return JsonSerializer.Serialize(content, JsonWeb);
    }

    private async Task<ExaResearchQueuedTask> QueueResearchTaskAsync(
        string instructions,
        string model,
        object? outputSchema,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["instructions"] = instructions
        };

        if (outputSchema is not null)
            payload["outputSchema"] = outputSchema;

        var json = JsonSerializer.Serialize(payload, JsonWeb);
        using var req = new HttpRequestMessage(HttpMethod.Post, "research/v1")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Exa research create failed ({(int)resp.StatusCode}): {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        var id = root.TryGetProperty("researchId", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Exa research create response missing researchId.");

        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString() ?? "pending"
            : "pending";

        return new ExaResearchQueuedTask
        {
            ResearchId = id!,
            Status = status
        };
    }

    private async Task<ExaResearchCompletedTask> WaitForResearchCompletionAsync(
        string researchId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"research/v1/{researchId}");
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var text = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Exa research poll failed ({(int)resp.StatusCode}): {text}");

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString()
                : null;

            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return ToCompletedTask(root);

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                    ? errEl.GetString()
                    : "Exa research task failed.";
                throw new InvalidOperationException(error);
            }

            if (string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Exa research task canceled.");

            await Task.Delay(800, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async IAsyncEnumerable<ExaResearchStreamEvent> StreamResearchEventsAsync(
        string instructions,
        string model,
        object? outputSchema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var queued = await QueueResearchTaskAsync(instructions, model, outputSchema, cancellationToken);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"research/v1/{queued.ResearchId}?stream=true");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Exa research stream failed ({(int)resp.StatusCode}): {err}");
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
            var evt = ParseStreamEvent(doc.RootElement, queued.ResearchId);
            if (evt is not null)
            {
                yield return evt;
                if (evt.IsTerminal)
                    yield break;
            }
        }
    }

    private static ExaResearchStreamEvent? ParseStreamEvent(JsonElement root, string fallbackId)
    {
        var researchId = root.TryGetProperty("researchId", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : fallbackId;

        var createdMs = root.TryGetProperty("createdAt", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number
            ? createdEl.GetInt64()
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var created = DateTimeOffset.FromUnixTimeMilliseconds(createdMs).ToUnixTimeSeconds();

        if (root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
        {
            var status = statusEl.GetString();
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractOutputFromStatus(root, researchId!, created, isTerminal: true);
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                    ? errEl.GetString()
                    : "Exa research failed";

                return new ExaResearchStreamEvent
                {
                    ResearchId = researchId!,
                    Created = created,
                    Error = error,
                    IsTerminal = true
                };
            }
        }

        if (root.TryGetProperty("eventType", out var eventTypeEl)
            && eventTypeEl.ValueKind == JsonValueKind.String)
        {
            var eventType = eventTypeEl.GetString() ?? string.Empty;
            if (string.Equals(eventType, "research-output", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractOutputFromEvent(root, researchId!, created, isTerminal: true);
            }

            if (string.Equals(eventType, "task-output", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractTaskOutput(root, researchId!, created);
            }
        }

        return null;
    }

    private static ExaResearchStreamEvent? ExtractOutputFromEvent(JsonElement root, string researchId, long created, bool isTerminal)
    {
        if (!root.TryGetProperty("output", out var outputEl) || outputEl.ValueKind != JsonValueKind.Object)
            return null;

        if (outputEl.TryGetProperty("content", out var contentEl))
        {
            if (contentEl.ValueKind == JsonValueKind.String)
            {
                return new ExaResearchStreamEvent
                {
                    ResearchId = researchId,
                    Created = created,
                    ContentText = contentEl.GetString(),
                    IsTerminal = isTerminal
                };
            }

            if (contentEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return new ExaResearchStreamEvent
                {
                    ResearchId = researchId,
                    Created = created,
                    ContentObject = contentEl.Clone(),
                    IsTerminal = isTerminal
                };
            }
        }

        if (outputEl.TryGetProperty("parsed", out var parsedEl)
            && parsedEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return new ExaResearchStreamEvent
            {
                ResearchId = researchId,
                Created = created,
                ContentObject = parsedEl.Clone(),
                IsTerminal = isTerminal
            };
        }

        return null;
    }

    private static ExaResearchStreamEvent? ExtractTaskOutput(JsonElement root, string researchId, long created)
    {
        if (!root.TryGetProperty("output", out var outputEl) || outputEl.ValueKind != JsonValueKind.Object)
            return null;

        if (outputEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
        {
            return new ExaResearchStreamEvent
            {
                ResearchId = researchId,
                Created = created,
                ContentText = contentEl.GetString(),
                IsTerminal = false
            };
        }

        return null;
    }

    private static ExaResearchStreamEvent ExtractOutputFromStatus(JsonElement root, string researchId, long created, bool isTerminal)
    {
        if (root.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.Object)
        {
            if (outputEl.TryGetProperty("content", out var contentEl))
            {
                if (contentEl.ValueKind == JsonValueKind.String)
                {
                    return new ExaResearchStreamEvent
                    {
                        ResearchId = researchId,
                        Created = created,
                        ContentText = contentEl.GetString(),
                        IsTerminal = isTerminal
                    };
                }

                if (contentEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return new ExaResearchStreamEvent
                    {
                        ResearchId = researchId,
                        Created = created,
                        ContentObject = contentEl.Clone(),
                        IsTerminal = isTerminal
                    };
                }
            }

            if (outputEl.TryGetProperty("parsed", out var parsedEl)
                && parsedEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return new ExaResearchStreamEvent
                {
                    ResearchId = researchId,
                    Created = created,
                    ContentObject = parsedEl.Clone(),
                    IsTerminal = isTerminal
                };
            }
        }

        return new ExaResearchStreamEvent
        {
            ResearchId = researchId,
            Created = created,
            IsTerminal = isTerminal
        };
    }

    private static ExaResearchCompletedTask ToCompletedTask(JsonElement root)
    {
        var researchId = root.TryGetProperty("researchId", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
            : Guid.NewGuid().ToString("n");

        var createdAt = root.TryGetProperty("createdAt", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(createdEl.GetInt64()).UtcDateTime
            : DateTime.UtcNow;

        var finishedAt = root.TryGetProperty("finishedAt", out var finishedEl) && finishedEl.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(finishedEl.GetInt64()).UtcDateTime
            : DateTime.UtcNow;

        object content = string.Empty;
        object? parsed = null;
        if (root.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.Object)
        {
            if (outputEl.TryGetProperty("content", out var contentEl))
            {
                content = contentEl.ValueKind == JsonValueKind.String
                    ? (object)(contentEl.GetString() ?? string.Empty)
                    : JsonSerializer.Deserialize<object>(contentEl.GetRawText(), JsonWeb) ?? string.Empty;
            }

            if (outputEl.TryGetProperty("parsed", out var parsedEl)
                && parsedEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                parsed = JsonSerializer.Deserialize<object>(parsedEl.GetRawText(), JsonWeb);
            }
        }

        var cost = root.TryGetProperty("costDollars", out var costEl) && costEl.ValueKind == JsonValueKind.Object
            ? costEl.Clone()
            : (JsonElement?)null;

        return new ExaResearchCompletedTask
        {
            ResearchId = researchId,
            CreatedAt = createdAt,
            FinishedAt = finishedAt,
            Content = content,
            Parsed = parsed,
            Cost = cost
        };
    }

    private static ResponseInput BuildResponseInputFromSampling(ModelContextProtocol.Protocol.CreateMessageRequestParams chatRequest)
    {
        var items = new List<ResponseInputItem>();

        foreach (var msg in chatRequest.Messages)
        {
            var parts = msg.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(a => new InputTextPart(a.Text))
                .Cast<ResponseContentPart>()
                .ToList();

            if (parts.Count == 0)
            {
                var text = msg.ToText();
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(new InputTextPart(text));
            }

            if (parts.Count == 0)
                continue;

            var role = msg.Role == ModelContextProtocol.Protocol.Role.Assistant
                ? ResponseRole.Assistant
                : ResponseRole.User;

            items.Add(new ResponseInputMessage
            {
                Role = role,
                Content = new ResponseMessageContent(parts)
            });
        }

        return new ResponseInput(items);
    }

    private sealed class ExaResearchQueuedTask
    {
        public string ResearchId { get; init; } = default!;

        public string Status { get; init; } = default!;
    }

    private sealed class ExaResearchCompletedTask
    {
        public string ResearchId { get; init; } = default!;

        public DateTime CreatedAt { get; init; }

        public DateTime FinishedAt { get; init; }

        public object Content { get; init; } = string.Empty;

        public object? Parsed { get; init; }

        public JsonElement? Cost { get; init; }
    }

    private sealed class ExaResearchStreamEvent
    {
        public string ResearchId { get; init; } = default!;

        public long Created { get; init; }

        public string? ContentText { get; init; }

        public JsonElement? ContentObject { get; init; }

        public string? Error { get; init; }

        public bool IsTerminal { get; init; }
    }
}
