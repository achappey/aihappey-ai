using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Inferencesh;

public partial class InferenceshProvider
{
    private static readonly JsonSerializerOptions InferenceJson = JsonSerializerOptions.Web;

    private async Task<InferenceTask> RunTaskAsync(
        string app,
        string prompt,
        float? temperature,
        int? maxOutputTokens,
        float? topP,
        CancellationToken cancellationToken,
        bool waitForTerminal = true)
    {
        var payload = new Dictionary<string, object?>
        {
            ["app"] = app,
            ["input"] = new Dictionary<string, object?>
            {
                ["prompt"] = prompt
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "run")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, InferenceJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Inference.sh run failed ({(int)resp.StatusCode}): {raw}");

        var task = ParseTaskFromRaw(raw);
        if (waitForTerminal && !IsTerminalStatus(task.Status))
            return await WaitForTaskTerminalAsync(task.Id, cancellationToken);

        return task;
    }

    private async Task<InferenceTask> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"tasks/{Uri.EscapeDataString(taskId)}");
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Inference.sh get task failed ({(int)resp.StatusCode}): {raw}");

        return ParseTaskFromRaw(raw);
    }

    private async Task<InferenceTask> WaitForTaskTerminalAsync(string taskId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var task = await GetTaskAsync(taskId, cancellationToken);
            if (IsTerminalStatus(task.Status))
                return task;

            await Task.Delay(800, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async IAsyncEnumerable<InferenceTaskTextUpdate> StreamTaskTextUpdatesAsync(
        string taskId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fullText = string.Empty;
        InferenceTask? lastTask = null;
        var terminalObserved = false;

        await foreach (var evt in StreamTaskEventsAsync(taskId, cancellationToken: cancellationToken))
        {
            if (evt.Task is not null)
            {
                lastTask = evt.Task;
                var currentText = ExtractTaskText(evt.Task);
                var delta = CalculateDelta(fullText, currentText);

                if (!string.IsNullOrWhiteSpace(delta))
                {
                    yield return new InferenceTaskTextUpdate
                    {
                        Task = evt.Task,
                        Delta = delta,
                        FullText = currentText,
                        IsTerminal = false,
                        IsSuccess = false
                    };
                }

                fullText = currentText;
            }

            if (!evt.IsTerminal)
                continue;

            terminalObserved = true;
            var terminalTask = evt.Task ?? lastTask ?? await GetTaskAsync(taskId, cancellationToken);
            var terminalText = ExtractTaskText(terminalTask);
            var terminalDelta = CalculateDelta(fullText, terminalText);

            if (!string.IsNullOrWhiteSpace(terminalDelta))
            {
                yield return new InferenceTaskTextUpdate
                {
                    Task = terminalTask,
                    Delta = terminalDelta,
                    FullText = terminalText,
                    IsTerminal = false,
                    IsSuccess = false
                };
            }

            yield return new InferenceTaskTextUpdate
            {
                Task = terminalTask,
                Delta = string.Empty,
                FullText = terminalText,
                IsTerminal = true,
                IsSuccess = evt.IsSuccess,
                Error = evt.Error ?? terminalTask.Error
            };

            yield break;
        }

        if (terminalObserved)
            yield break;

        var finalTask = await GetTaskAsync(taskId, cancellationToken);
        var finalText = ExtractTaskText(finalTask);
        var finalDelta = CalculateDelta(fullText, finalText);
        if (!string.IsNullOrWhiteSpace(finalDelta))
        {
            yield return new InferenceTaskTextUpdate
            {
                Task = finalTask,
                Delta = finalDelta,
                FullText = finalText,
                IsTerminal = false,
                IsSuccess = false
            };
        }

        yield return new InferenceTaskTextUpdate
        {
            Task = finalTask,
            Delta = string.Empty,
            FullText = finalText,
            IsTerminal = true,
            IsSuccess = IsSuccessStatus(finalTask.Status),
            Error = finalTask.Error
        };
    }

    private async IAsyncEnumerable<InferenceTaskStreamEvent> StreamTaskEventsAsync(
        string taskId,
        string? lastEventId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"tasks/{Uri.EscapeDataString(taskId)}/stream");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (!string.IsNullOrWhiteSpace(lastEventId))
            req.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var rawError = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Inference.sh stream failed ({(int)resp.StatusCode}): {rawError}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var eventType = "message";
        string? eventId = null;
        var dataLines = new List<string>();
        InferenceTask? lastTask = null;

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync();
            if (line is null)
                break;

            if (line.Length == 0)
            {
                var parsed = ParseSseEvent(eventType, eventId, dataLines, lastTask);
                if (parsed is not null)
                {
                    if (parsed.Task is not null)
                        lastTask = parsed.Task;

                    yield return parsed;
                    if (parsed.IsTerminal)
                        yield break;
                }

                eventType = "message";
                eventId = null;
                dataLines.Clear();
                continue;
            }

            if (line.StartsWith(':'))
                continue;

            var sepIdx = line.IndexOf(':');
            if (sepIdx < 0)
                continue;

            var field = line[..sepIdx].Trim();
            var value = line[(sepIdx + 1)..].TrimStart();
            switch (field)
            {
                case "event":
                    eventType = string.IsNullOrWhiteSpace(value) ? "message" : value;
                    break;
                case "id":
                    eventId = value;
                    break;
                case "data":
                    dataLines.Add(value);
                    break;
            }
        }
    }

    private static InferenceTaskStreamEvent? ParseSseEvent(
        string eventType,
        string? eventId,
        List<string> dataLines,
        InferenceTask? lastTask)
    {
        var data = string.Join("\n", dataLines);
        if (string.IsNullOrWhiteSpace(eventType))
            eventType = "message";

        if (string.Equals(eventType, "update", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryExtractTaskFromData(data, out var task) || task is null)
                return null;

            return new InferenceTaskStreamEvent
            {
                EventType = "update",
                EventId = eventId,
                Task = task,
                IsTerminal = IsTerminalStatus(task.Status),
                IsSuccess = IsSuccessStatus(task.Status)
            };
        }

        if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
        {
            return new InferenceTaskStreamEvent
            {
                EventType = "error",
                EventId = eventId,
                Task = lastTask,
                IsTerminal = true,
                IsSuccess = false,
                Error = ExtractSseError(data)
            };
        }

        if (string.Equals(eventType, "done", StringComparison.OrdinalIgnoreCase))
        {
            return new InferenceTaskStreamEvent
            {
                EventType = "done",
                EventId = eventId,
                Task = lastTask,
                IsTerminal = lastTask is not null && IsTerminalStatus(lastTask.Status),
                IsSuccess = lastTask is not null && IsSuccessStatus(lastTask.Status)
            };
        }

        return null;
    }

    private static string ExtractSseError(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return "Inference.sh stream error.";

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    return e.GetString() ?? "Inference.sh stream error.";

                if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString() ?? "Inference.sh stream error.";
            }
        }
        catch
        {
            // keep raw text fallback
        }

        return data;
    }

    private static bool TryExtractTaskFromData(string data, out InferenceTask? task)
    {
        task = null;
        if (string.IsNullOrWhiteSpace(data))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("task", out var taskEl) && taskEl.ValueKind == JsonValueKind.Object)
                {
                    task = ParseTask(taskEl);
                    return true;
                }

                if (root.TryGetProperty("id", out _))
                {
                    task = ParseTask(root);
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static InferenceTask ParseTaskFromRaw(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return ParseTask(doc.RootElement);
    }

    private static InferenceTask ParseTask(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
            : Guid.NewGuid().ToString("n");

        var status = root.TryGetProperty("status", out var statusEl) && statusEl.TryGetInt32(out var s)
            ? s
            : 0;

        var output = root.TryGetProperty("output", out var outputEl)
            ? outputEl.Clone()
            : default;

        var error = root.TryGetProperty("error", out var errorEl)
            ? errorEl.ValueKind switch
            {
                JsonValueKind.String => errorEl.GetString(),
                JsonValueKind.Object or JsonValueKind.Array => errorEl.GetRawText(),
                _ => null
            }
            : null;

        var createdAt = root.TryGetProperty("created_at", out var createdAtEl) && createdAtEl.ValueKind == JsonValueKind.String
            ? createdAtEl.GetString()
            : null;

        return new InferenceTask
        {
            Id = id,
            Status = status,
            Output = output,
            Error = error,
            CreatedAt = createdAt
        };
    }

    private static bool IsTerminalStatus(int status)
        => status is 9 or 10 or 11;

    private static bool IsSuccessStatus(int status)
        => status == 9;

    private static string CalculateDelta(string previous, string current)
    {
        if (string.IsNullOrEmpty(current))
            return string.Empty;

        if (string.IsNullOrEmpty(previous))
            return current;

        if (current.StartsWith(previous, StringComparison.Ordinal))
            return current[previous.Length..];

        return current;
    }

    private static long ToUnixTimeOrNow(string? iso)
        => DateTimeOffset.TryParse(iso, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static object? TryBuildUsage(JsonElement output)
    {
        var promptTokens = FindInt(output, "prompt_tokens") ?? FindInt(output, "input_tokens");
        var completionTokens = FindInt(output, "completion_tokens") ?? FindInt(output, "output_tokens");
        var totalTokens = FindInt(output, "total_tokens");

        if (promptTokens is null && completionTokens is null && totalTokens is null)
            return null;

        var pt = promptTokens ?? 0;
        var ct = completionTokens ?? 0;
        var tt = totalTokens ?? (pt + ct);

        return new
        {
            prompt_tokens = pt,
            completion_tokens = ct,
            total_tokens = tt
        };
    }

    private static int? FindInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var n))
                        return n;

                    if (prop.Value.ValueKind == JsonValueKind.String && int.TryParse(prop.Value.GetString(), out var ns))
                        return ns;
                }

                var nested = FindInt(prop.Value, propertyName);
                if (nested is not null)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindInt(item, propertyName);
                if (nested is not null)
                    return nested;
            }
        }

        return null;
    }

    private static string ExtractTaskText(InferenceTask task)
    {
        if (task.Output.ValueKind == JsonValueKind.Undefined || task.Output.ValueKind == JsonValueKind.Null)
            return string.Empty;

        var preferred = new[]
        {
            "text", "output_text", "content", "message", "answer", "result"
        };

        foreach (var name in preferred)
        {
            var value = FindStringByKey(task.Output, name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        var any = FindFirstString(task.Output);
        return any ?? string.Empty;
    }

    private static string? FindStringByKey(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString();

                    var nestedDirect = FindFirstString(prop.Value);
                    if (!string.IsNullOrWhiteSpace(nestedDirect))
                        return nestedDirect;
                }

                var nested = FindStringByKey(prop.Value, key);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindStringByKey(item, key);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        return null;
    }

    private static string? FindFirstString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object => element.EnumerateObject()
                .Select(p => FindFirstString(p.Value))
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(FindFirstString)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            _ => null
        };
    }

    private static string BuildPromptFromUiMessages(IEnumerable<UIMessage> messages)
    {
        var lines = new List<string>();
        foreach (var message in messages ?? [])
        {
            var text = string.Join("\n", message.Parts
                .OfType<TextUIPart>()
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));

            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{message.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage> messages)
    {
        var lines = new List<string>();
        foreach (var message in messages ?? [])
        {
            var text = ChatMessageContentExtensions.ToText(message.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add($"{message.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromResponseRequest(ResponseRequest request)
    {
        if (request.Input?.IsText == true)
            return request.Input.Text ?? string.Empty;

        if (request.Input?.IsItems == true && request.Input.Items is not null)
        {
            var lines = new List<string>();
            foreach (var item in request.Input.Items)
            {
                if (item is not ResponseInputMessage message)
                    continue;

                var role = message.Role.ToString().ToLowerInvariant();
                var text = message.Content.IsText
                    ? message.Content.Text
                    : string.Join("\n", message.Content.Parts?
                        .OfType<InputTextPart>()
                        .Select(p => p.Text) ?? []);

                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add($"{role}: {text}");
            }

            if (lines.Count > 0)
                return string.Join("\n\n", lines);
        }

        return request.Instructions ?? string.Empty;
    }

    private static string ResolveInferenceAppId(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var parts = model.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && string.Equals(parts[0], "inferencesh", StringComparison.OrdinalIgnoreCase))
            return string.Join('/', parts.Skip(1));

        return model;
    }

    private sealed class InferenceTask
    {
        public string Id { get; init; } = default!;
        public int Status { get; init; }
        public JsonElement Output { get; init; }
        public string? Error { get; init; }
        public string? CreatedAt { get; init; }
    }

    private sealed class InferenceTaskStreamEvent
    {
        public string EventType { get; init; } = default!;
        public string? EventId { get; init; }
        public InferenceTask? Task { get; init; }
        public bool IsTerminal { get; init; }
        public bool IsSuccess { get; init; }
        public string? Error { get; init; }
    }

    private sealed class InferenceTaskTextUpdate
    {
        public InferenceTask Task { get; init; } = default!;
        public string Delta { get; init; } = string.Empty;
        public string FullText { get; init; } = string.Empty;
        public bool IsTerminal { get; init; }
        public bool IsSuccess { get; init; }
        public string? Error { get; init; }
    }
}

