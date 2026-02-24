using System.Net;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BrowserUse;

public partial class BrowserUseProvider
{
    private static readonly JsonSerializerOptions BrowserUseJson = JsonSerializerOptions.Web;


    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ExecuteResponsesAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ExecuteResponsesStreamingAsync(options, cancellationToken);
    }

    private async Task<ResponseResult> ExecuteResponsesAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        var model = options.Model;
        var prompt = BuildPromptFromResponseRequest(options);

        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("BrowserUse requires non-empty input.");

        BrowserUseTaskCreatedResponse? created = null;
        try
        {
            created = await CreateTaskAsync(new BrowserUseCreateTaskRequest
            {
                Task = prompt,
                Llm = model,
                MaxSteps = options.MaxOutputTokens ?? 100,
                StructuredOutput = TryExtractStructuredOutputSchemaString(options.Text)
            }, cancellationToken);

            var terminal = await WaitForTaskTerminalAsync(created.Id, cancellationToken);
            var full = await GetTaskAsync(created.Id, cancellationToken);
            return ToResponseResult(full, options, terminal);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(created?.SessionId))
                await DeleteSessionSafeAsync(created!.SessionId!, cancellationToken);
        }
    }

    private async IAsyncEnumerable<ResponseStreamPart> ExecuteResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = options.Model;
        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("BrowserUse requires non-empty input.");

        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var responseId = Guid.NewGuid().ToString("n");
        var itemId = $"msg_{responseId}";
        var sequence = 1;
        var emittedStepNumber = 0;
        var fullText = new StringBuilder();

        BrowserUseTaskCreatedResponse? created = null;
        BrowserUseTaskView? lastTask = null;

        var inProgressResponse = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            Model = options.Model!,
            Temperature = options.Temperature,
            Metadata = options.Metadata,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls
        };

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = inProgressResponse
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = inProgressResponse
        };


        created = await CreateTaskAsync(new BrowserUseCreateTaskRequest
        {
            Task = prompt,
            Llm = model,
            MaxSteps = options.MaxOutputTokens ?? 100,
            StructuredOutput = TryExtractStructuredOutputSchemaString(options.Text)
        }, cancellationToken);

        responseId = created.Id;
        itemId = $"msg_{responseId}";

        while (!cancellationToken.IsCancellationRequested)
        {
            lastTask = await GetTaskAsync(created.Id, cancellationToken);

            foreach (var step in lastTask.Steps
                .Where(s => s.Number > emittedStepNumber)
                .OrderBy(s => s.Number))
            {
                var stepText = FormatStep(step);
                if (string.IsNullOrWhiteSpace(stepText))
                    continue;

                emittedStepNumber = Math.Max(emittedStepNumber, step.Number);
                fullText.Append(stepText).Append('\n');

                yield return new ResponseOutputTextDelta
                {
                    SequenceNumber = sequence++,
                    ItemId = itemId,
                    Outputindex = 0,
                    ContentIndex = 0,
                    Delta = stepText + "\n"
                };
            }

            if (IsTerminal(lastTask.Status))
                break;

            await Task.Delay(800, cancellationToken);
        }

        if (lastTask is null)
            throw new InvalidOperationException("BrowserUse task polling ended without a task state.");

        var finalText = ResolveFinalOutput(lastTask, fullText);

        yield return new ResponseOutputTextDone
        {
            SequenceNumber = sequence++,
            ItemId = itemId,
            Outputindex = 0,
            ContentIndex = 0,
            Text = finalText
        };

        var terminal = await GetTaskStatusAsync(created.Id, cancellationToken);
        var finalResult = ToResponseResult(lastTask, options, terminal);

        if (IsFinished(lastTask.Status))
        {
            yield return new ResponseCompleted
            {
                SequenceNumber = sequence,
                Response = finalResult
            };
        }
        else
        {
            yield return new ResponseFailed
            {
                SequenceNumber = sequence,
                Response = finalResult
            };
        }

        if (!string.IsNullOrWhiteSpace(created?.SessionId))
            await DeleteSessionSafeAsync(created!.SessionId!, cancellationToken);

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

    private ResponseResult ToResponseResult(BrowserUseTaskView task, ResponseRequest request, BrowserUseTaskStatusView status)
    {
        var text = ResolveFinalOutput(task, null);
        var createdAt = ToUnixTime(task.CreatedAt);
        var completedAt = ParseUnixTimeOrNow(status.FinishedAt);
        var isCompleted = IsFinished(status.Status);

        return new ResponseResult
        {
            Id = task.Id,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Status = isCompleted ? "completed" : "failed",
            Model = request.Model ?? task.Llm,
            Temperature = request.Temperature,
            Metadata = MergeMetadata(request.Metadata, task, status),
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            ParallelToolCalls = request.ParallelToolCalls,
            Usage = new
            {
                cost = status.Cost,
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0
            },
            Error = isCompleted
                ? null
                : new ResponseResultError
                {
                    Code = "browseruse_task_stopped",
                    Message = string.IsNullOrWhiteSpace(status.Output)
                        ? "BrowserUse task stopped before completion."
                        : status.Output
                },
            Output =
            [
                new
                {
                    id = $"msg_{task.Id}",
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text
                        }
                    }
                }
            ]
        };
    }

    private static Dictionary<string, object?> MergeMetadata(
        Dictionary<string, object?>? current,
        BrowserUseTaskView task,
        BrowserUseTaskStatusView status)
    {
        var merged = current is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(current);

        merged["browseruse_task_id"] = task.Id;
        merged["browseruse_session_id"] = task.SessionId;
        merged["browseruse_status"] = status.Status;
        merged["browseruse_is_success"] = status.IsSuccess;
        merged["browseruse_cost"] = status.Cost;

        return merged;
    }

    private static string ResolveFinalOutput(BrowserUseTaskView task, StringBuilder? fallbackBuilder)
    {
        if (!string.IsNullOrWhiteSpace(task.Output))
            return task.Output!;

        if (fallbackBuilder is not null && fallbackBuilder.Length > 0)
            return fallbackBuilder.ToString().Trim();

        var fromSteps = string.Join('\n', task.Steps
            .OrderBy(s => s.Number)
            .Select(FormatStep)
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        return fromSteps;
    }

    private static string FormatStep(BrowserUseTaskStepView step)
    {
        var actions = step.Actions.Count == 0
            ? string.Empty
            : $" Actions: {string.Join(" | ", step.Actions)}.";

        var goal = string.IsNullOrWhiteSpace(step.NextGoal)
            ? step.EvaluationPreviousGoal ?? step.Memory ?? ""
            : step.NextGoal!;

        var url = string.IsNullOrWhiteSpace(step.Url) ? string.Empty : $" URL: {step.Url}.";

        return $"Step {step.Number}: {goal}.{url}{actions}".Trim();
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

