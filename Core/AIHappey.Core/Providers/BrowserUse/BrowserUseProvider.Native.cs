using System.Net;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BrowserUse;

public partial class BrowserUseProvider
{
    private static readonly JsonSerializerOptions BrowserUseJson = JsonSerializerOptions.Web;

    private sealed class BrowserUseNativeTerminalResult
    {
        public BrowserUseTaskCreatedResponse Created { get; init; } = default!;
        public BrowserUseTaskView Task { get; init; } = default!;
        public BrowserUseTaskStatusView Status { get; init; } = default!;
        public string OutputText { get; init; } = string.Empty;
        public string? DoneText { get; init; }
    }

    private abstract class BrowserUseNativeStreamEvent;

    private sealed class BrowserUseNativeCreatedStreamEvent : BrowserUseNativeStreamEvent
    {
        public BrowserUseTaskCreatedResponse Created { get; init; } = default!;
    }

    private sealed class BrowserUseNativeActionStreamEvent : BrowserUseNativeStreamEvent
    {
        public BrowserUseNativeActionEvent Action { get; init; } = default!;
    }

    private sealed class BrowserUseNativeTerminalStreamEvent : BrowserUseNativeStreamEvent
    {
        public BrowserUseNativeTerminalResult Terminal { get; init; } = default!;
    }

    private sealed class BrowserUseNativeActionEvent
    {
        public string TaskId { get; init; } = default!;
        public int StepNumber { get; init; }
        public int ActionIndex { get; init; }
        public string ToolCallId { get; init; } = default!;
        public string ToolName { get; init; } = default!;
        public object Input { get; init; } = default!;
        public object Output { get; init; } = default!;
        public bool IsDone { get; init; }
        public string? DoneText { get; init; }
    }

    private sealed class BrowserUseParsedAction
    {
        public string ToolName { get; init; } = "action";
        public object Input { get; init; } = new { };
        public bool IsDone { get; init; }
        public string? DoneText { get; init; }
        public bool? DoneSuccess { get; init; }
    }

    private async Task<BrowserUseNativeTerminalResult> ExecuteNativeTaskAsync(BrowserUseCreateTaskRequest request, CancellationToken cancellationToken)
    {
        BrowserUseTaskCreatedResponse? created = null;

        try
        {
            created = await CreateTaskAsync(request, cancellationToken);
            var status = await WaitForTaskTerminalAsync(created.Id, cancellationToken);
            var task = await GetTaskAsync(created.Id, cancellationToken);

            var doneText = TryExtractLatestDoneText(task);
            var outputText = ResolveFinalOutput(task, fallbackBuilder: null, doneText);

            return new BrowserUseNativeTerminalResult
            {
                Created = created,
                Task = task,
                Status = status,
                DoneText = doneText,
                OutputText = outputText
            };
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(created?.SessionId))
                await DeleteSessionSafeAsync(created!.SessionId!, cancellationToken);
        }
    }

    private async IAsyncEnumerable<BrowserUseNativeStreamEvent> StreamNativeTaskAsync(
        BrowserUseCreateTaskRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        BrowserUseTaskCreatedResponse? created = null;
        BrowserUseTaskView? lastTask = null;
        string? latestDoneText = null;
        var emittedStepNumber = 0;

        try
        {
            created = await CreateTaskAsync(request, cancellationToken);

            yield return new BrowserUseNativeCreatedStreamEvent
            {
                Created = created
            };

            while (!cancellationToken.IsCancellationRequested)
            {
                var status = await GetTaskStatusAsync(created.Id, cancellationToken);
                lastTask = await GetTaskAsync(created.Id, cancellationToken);

                foreach (var actionEvent in ExtractActionEvents(lastTask, emittedStepNumber))
                {
                    emittedStepNumber = Math.Max(emittedStepNumber, actionEvent.StepNumber);

                    if (actionEvent.IsDone && !string.IsNullOrWhiteSpace(actionEvent.DoneText))
                        latestDoneText = actionEvent.DoneText;

                    yield return new BrowserUseNativeActionStreamEvent
                    {
                        Action = actionEvent
                    };
                }

                if (IsTerminal(status.Status))
                {
                    var outputText = ResolveFinalOutput(lastTask, fallbackBuilder: null, latestDoneText);

                    yield return new BrowserUseNativeTerminalStreamEvent
                    {
                        Terminal = new BrowserUseNativeTerminalResult
                        {
                            Created = created,
                            Task = lastTask,
                            Status = status,
                            DoneText = latestDoneText,
                            OutputText = outputText
                        }
                    };

                    yield break;
                }

                await Task.Delay(800, cancellationToken);
            }

            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(created?.SessionId))
                await DeleteSessionSafeAsync(created!.SessionId!, cancellationToken);
        }
    }

    private static IEnumerable<BrowserUseNativeActionEvent> ExtractActionEvents(
        BrowserUseTaskView task,
        int emittedStepNumber)
    {
        foreach (var step in task.Steps
            .Where(s => s.Number > emittedStepNumber)
            .OrderBy(s => s.Number))
        {
            for (var i = 0; i < step.Actions.Count; i++)
            {
                var parsed = ParseStepAction(step.Actions[i]);
                var callId = $"bu_{task.Id}_s{step.Number}_a{i + 1}";

                var output = new
                {
                    success = parsed.IsDone ? parsed.DoneSuccess : (bool?)true,
                    text = parsed.IsDone ? parsed.DoneText : null,
                    step = step.Number,
                    url = step.Url,
                    action = parsed.IsDone ? null : parsed.ToolName
                };

                yield return new BrowserUseNativeActionEvent
                {
                    TaskId = task.Id,
                    StepNumber = step.Number,
                    ActionIndex = i,
                    ToolCallId = callId,
                    ToolName = parsed.ToolName,
                    Input = parsed.Input,
                    Output = output,
                    IsDone = parsed.IsDone,
                    DoneText = parsed.DoneText
                };
            }
        }
    }

    private static BrowserUseParsedAction ParseStepAction(string rawAction)
    {
        if (string.IsNullOrWhiteSpace(rawAction))
        {
            return new BrowserUseParsedAction
            {
                ToolName = "action",
                Input = new { }
            };
        }

        try
        {
            using var doc = JsonDocument.Parse(rawAction);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new BrowserUseParsedAction
                {
                    ToolName = "action",
                    Input = DeserializeUntyped(root)
                };
            }

            var first = root.EnumerateObject().FirstOrDefault();
            if (string.IsNullOrWhiteSpace(first.Name))
            {
                return new BrowserUseParsedAction
                {
                    ToolName = "action",
                    Input = DeserializeUntyped(root)
                };
            }

            var isDone = string.Equals(first.Name, "done", StringComparison.OrdinalIgnoreCase);
            var doneText = isDone ? ExtractDoneText(first.Value) : null;
            var doneSuccess = isDone ? ExtractDoneSuccess(first.Value) : null;

            return new BrowserUseParsedAction
            {
                ToolName = first.Name,
                Input = DeserializeUntyped(first.Value),
                IsDone = isDone,
                DoneText = doneText,
                DoneSuccess = doneSuccess
            };
        }
        catch
        {
            return new BrowserUseParsedAction
            {
                ToolName = "action",
                Input = new { raw = rawAction }
            };
        }
    }

    private static object DeserializeUntyped(JsonElement element)
        => JsonSerializer.Deserialize<object>(element.GetRawText(), BrowserUseJson) ?? new { };

    private static bool? ExtractDoneSuccess(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("success", out var success)
            && (success.ValueKind == JsonValueKind.True || success.ValueKind == JsonValueKind.False))
        {
            return success.GetBoolean();
        }

        return null;
    }

    private static string? ExtractDoneText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind != JsonValueKind.Object)
            return null;

        if (value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString();

        if (value.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String)
            return output.GetString();

        if (value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
            return result.GetString();

        if (value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString();

        return null;
    }

    private static string? TryExtractLatestDoneText(BrowserUseTaskView task)
    {
        foreach (var step in task.Steps.OrderByDescending(s => s.Number))
        {
            for (var i = step.Actions.Count - 1; i >= 0; i--)
            {
                var parsed = ParseStepAction(step.Actions[i]);
                if (parsed.IsDone && !string.IsNullOrWhiteSpace(parsed.DoneText))
                    return parsed.DoneText;
            }
        }

        return null;
    }

    private static string ResolveFinalOutput(BrowserUseTaskView task, StringBuilder? fallbackBuilder, string? doneText)
    {
        if (!string.IsNullOrWhiteSpace(task.Output))
        {
            var normalized = NormalizeTaskOutput(task.Output!);
            return normalized ?? task.Output!;
        }

        if (!string.IsNullOrWhiteSpace(doneText))
            return doneText!;

        if (fallbackBuilder is not null && fallbackBuilder.Length > 0)
            return fallbackBuilder.ToString().Trim();

        var fromSteps = string.Join('\n', task.Steps
            .OrderBy(s => s.Number)
            .Select(FormatStepSummary)
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        return fromSteps;
    }

    private static string? NormalizeTaskOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var trimmed = output.Trim();

        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return output;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("done", out var done))
                {
                    var doneText = ExtractDoneText(done);
                    if (!string.IsNullOrWhiteSpace(doneText))
                        return doneText;
                }

                var fallbackText = ExtractDoneText(root);
                if (!string.IsNullOrWhiteSpace(fallbackText))
                    return fallbackText;
            }
        }
        catch
        {
            // keep raw output if JSON parsing fails
        }

        return output;
    }

    private static string FormatStepSummary(BrowserUseTaskStepView step)
    {
        var actionNames = step.Actions
            .Select(ParseStepAction)
            .Select(a => a.ToolName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        var actions = actionNames.Length == 0
            ? string.Empty
            : $" Actions: {string.Join(", ", actionNames)}.";

        var goal = string.IsNullOrWhiteSpace(step.NextGoal)
            ? step.EvaluationPreviousGoal ?? step.Memory ?? string.Empty
            : step.NextGoal!;

        var url = string.IsNullOrWhiteSpace(step.Url)
            ? string.Empty
            : $" URL: {step.Url}.";

        return $"Step {step.Number}: {goal}.{url}{actions}".Trim();
    }

    private async Task<BrowserUseTaskCreatedResponse> CreateTaskAsync(BrowserUseCreateTaskRequest request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request, BrowserUseJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v2/tasks")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (resp.StatusCode != HttpStatusCode.Accepted)
            throw new HttpRequestException($"BrowserUse create task failed ({(int)resp.StatusCode}): {raw}");

        return JsonSerializer.Deserialize<BrowserUseTaskCreatedResponse>(raw, BrowserUseJson)
               ?? throw new InvalidOperationException("BrowserUse create task returned empty payload.");
    }

    private async Task<BrowserUseTaskStatusView> GetTaskStatusAsync(string taskId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/v2/tasks/{Uri.EscapeDataString(taskId)}/status");
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"BrowserUse task status failed ({(int)resp.StatusCode}): {raw}");

        return JsonSerializer.Deserialize<BrowserUseTaskStatusView>(raw, BrowserUseJson)
               ?? throw new InvalidOperationException("BrowserUse task status payload was empty.");
    }

    private async Task<BrowserUseTaskView> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/v2/tasks/{Uri.EscapeDataString(taskId)}");
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"BrowserUse get task failed ({(int)resp.StatusCode}): {raw}");

        return JsonSerializer.Deserialize<BrowserUseTaskView>(raw, BrowserUseJson)
               ?? throw new InvalidOperationException("BrowserUse get task payload was empty.");
    }

    private async Task<BrowserUseTaskStatusView> WaitForTaskTerminalAsync(string taskId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = await GetTaskStatusAsync(taskId, cancellationToken);
            if (IsTerminal(status.Status))
                return status;

            await Task.Delay(800, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task DeleteSessionSafeAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"api/v2/sessions/{Uri.EscapeDataString(sessionId)}");
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return;

            if (!resp.IsSuccessStatusCode)
            {
                var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"BrowserUse delete session failed ({(int)resp.StatusCode}): {raw}");
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    private static bool IsTerminal(string status)
        => string.Equals(status, "finished", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinished(string status)
        => string.Equals(status, "finished", StringComparison.OrdinalIgnoreCase);

    private static long ToUnixTime(string? dateTimeUtc)
        => DateTimeOffset.TryParse(dateTimeUtc, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static long ParseUnixTimeOrNow(string? dateTimeUtc)
        => DateTimeOffset.TryParse(dateTimeUtc, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

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

    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage> messages)
    {
        var lines = new List<string>();
        foreach (var message in messages ?? [])
        {
            var text = message.Content.GetRawText();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add($"{message.Role}: {text}");
        }

        return string.Join("\n\n", lines);
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

    private static string? TryExtractStructuredOutputSchemaString(object? format)
    {
        if (format is null)
            return null;

        var schema = format.GetJSONSchema();
        if (schema?.JsonSchema?.Schema is JsonElement element
            && element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return element.GetRawText();
        }

        return null;
    }

    private static string ExtractOutputText(IEnumerable<object> output)
    {
        var first = output.FirstOrDefault();
        if (first is null)
            return string.Empty;

        var element = JsonSerializer.SerializeToElement(first, BrowserUseJson);
        if (!element.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                return textEl.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private sealed class BrowserUseCreateTaskRequest
    {
        [JsonPropertyName("task")]
        public string Task { get; init; } = default!;

        [JsonPropertyName("llm")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Llm { get; init; }

        [JsonPropertyName("maxSteps")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxSteps { get; init; }

        [JsonPropertyName("structuredOutput")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StructuredOutput { get; init; }
    }

    private sealed class BrowserUseTaskCreatedResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = default!;

        [JsonPropertyName("sessionId")]
        public string SessionId { get; init; } = default!;
    }

    private sealed class BrowserUseTaskStatusView
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = default!;

        [JsonPropertyName("status")]
        public string Status { get; init; } = default!;

        [JsonPropertyName("output")]
        public string? Output { get; init; }

        [JsonPropertyName("finishedAt")]
        public string? FinishedAt { get; init; }

        [JsonPropertyName("isSuccess")]
        public bool? IsSuccess { get; init; }

        [JsonPropertyName("cost")]
        public string? Cost { get; init; }
    }

    private sealed class BrowserUseTaskView
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = default!;

        [JsonPropertyName("sessionId")]
        public string SessionId { get; init; } = default!;

        [JsonPropertyName("llm")]
        public string Llm { get; init; } = default!;

        [JsonPropertyName("status")]
        public string Status { get; init; } = default!;

        [JsonPropertyName("createdAt")]
        public string? CreatedAt { get; init; }

        [JsonPropertyName("output")]
        public string? Output { get; init; }

        [JsonPropertyName("steps")]
        public List<BrowserUseTaskStepView> Steps { get; init; } = [];
    }

    private sealed class BrowserUseTaskStepView
    {
        [JsonPropertyName("number")]
        public int Number { get; init; }

        [JsonPropertyName("memory")]
        public string? Memory { get; init; }

        [JsonPropertyName("evaluationPreviousGoal")]
        public string? EvaluationPreviousGoal { get; init; }

        [JsonPropertyName("nextGoal")]
        public string? NextGoal { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("actions")]
        public List<string> Actions { get; init; } = [];
    }
}

