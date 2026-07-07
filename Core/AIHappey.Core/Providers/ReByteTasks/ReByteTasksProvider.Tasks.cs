using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.ReByteTasks;

public partial class ReByteTasksProvider
{
    private const string ReByteTasksEndpoint = "v1/tasks";
    private const string ReByteTaskToolName = "rebyte_task";
    private const string ReByteFollowUpToolName = "rebyte_task_follow_up";
    private const int DefaultReByteTaskPollIntervalMilliseconds = 2_000;
    private const int DefaultReByteTaskPollTimeoutSeconds = 600;

    private static readonly JsonSerializerOptions ReByteTaskJson = JsonSerializerOptions.Web;
    private static readonly HashSet<string> ReByteTaskExecutors = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude",
        "codex",
        "gemini",
        "opencode"
    };

    private sealed record ReByteTaskTarget(string Executor, string? Model, string? RequestModel);

    private sealed record ReByteTaskState(
        string? TaskId,
        string? WorkspaceId,
        string? PromptId,
        string? Url,
        JsonElement? Raw);

    private sealed record ReByteTaskSubmission(
        string Operation,
        string ToolName,
        string ToolTitle,
        string ToolCallId,
        JsonElement Payload,
        ReByteTaskState State);

    private sealed class ReByteSseEvent
    {
        public string Event { get; init; } = "event";

        public string? Id { get; init; }

        public string Data { get; init; } = string.Empty;
    }

    private async Task<AIResponse> ExecuteReByteTaskUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var target = ResolveReByteTaskTarget(request);
        var existingState = ResolveReByteTaskState(request);
        var isFollowUp = !string.IsNullOrWhiteSpace(existingState.TaskId);
        var prompt = BuildReByteTaskPrompt(request, followUp: isFollowUp);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("ReByte Tasks requires a non-empty prompt derived from unified input or instructions.");

        var payload = isFollowUp
            ? BuildReByteTaskFollowUpPayload(request, prompt)
            : BuildReByteTaskCreatePayload(request, target, prompt, existingState.WorkspaceId);

        var submittedPayload = JsonSerializer.SerializeToElement(payload, ReByteTaskJson);
        ReByteTaskSubmission? submission = null;
        JsonElement? finalTask = null;
        var pollAttempt = 0;

        try
        {
            submission = await SubmitReByteTaskAsync(
                request,
                isFollowUp,
                existingState,
                submittedPayload,
                payload,
                cancellationToken);

            finalTask = await PollReByteTaskUntilTerminalAsync(
                submission.State.TaskId!,
                attempt => pollAttempt = attempt,
                cancellationToken);

            return ToReByteTaskUnifiedResponse(request, target, submission, finalTask.Value, pollAttempt);
        }
        catch (OperationCanceledException) when (!string.IsNullOrWhiteSpace(submission?.State.TaskId ?? existingState.TaskId))
        {
            await CancelReByteTaskSafeAsync(submission?.State.TaskId ?? existingState.TaskId!, CancellationToken.None);
            throw;
        }
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamReByteTaskUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var providerId = GetIdentifier();
        var target = ResolveReByteTaskTarget(request);
        var existingState = ResolveReByteTaskState(request);
        var isFollowUp = !string.IsNullOrWhiteSpace(existingState.TaskId);
        var prompt = BuildReByteTaskPrompt(request, followUp: isFollowUp);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("ReByte Tasks requires a non-empty prompt derived from unified input or instructions.");

        var payload = isFollowUp
            ? BuildReByteTaskFollowUpPayload(request, prompt)
            : BuildReByteTaskCreatePayload(request, target, prompt, existingState.WorkspaceId);

        var submittedPayloadJson = JsonSerializer.Serialize(payload, ReByteTaskJson);
        var submittedPayload = JsonSerializer.SerializeToElement(payload, ReByteTaskJson);
        var operation = isFollowUp ? "follow_up" : "create_task";
        var toolName = isFollowUp ? ReByteFollowUpToolName : ReByteTaskToolName;
        var toolTitle = isFollowUp ? "Send ReByte task follow-up" : "Create ReByte task";
        var eventId = request.Id ?? $"rebyte_task_{Guid.NewGuid():N}";
        var toolCallId = BuildReByteTaskToolCallId(operation, eventId);
        var startedAt = DateTimeOffset.UtcNow;
        var metadata = BuildReByteTaskMetadata(request, target, operation, submittedPayload, existingState, null, 0, toolCallId);
        ReByteTaskSubmission? submission = null;

        yield return CreateReByteTaskStreamEvent(
            providerId,
            toolCallId,
            "tool-input-start",
            new AIToolInputStartEventData
            {
                ToolName = toolName,
                Title = toolTitle,
                ProviderExecuted = true,
                ProviderMetadata = CreateReByteTaskToolProviderMetadata(providerId, toolName, toolTitle, toolCallId, "tool_use")
            },
            startedAt,
            metadata);

        yield return CreateReByteTaskStreamEvent(
            providerId,
            toolCallId,
            "tool-input-delta",
            new AIToolInputDeltaEventData { InputTextDelta = submittedPayloadJson },
            startedAt,
            metadata);

        yield return CreateReByteTaskStreamEvent(
            providerId,
            toolCallId,
            "tool-input-available",
            new AIToolInputAvailableEventData
            {
                ToolName = toolName,
                Title = toolTitle,
                ProviderExecuted = true,
                Input = submittedPayload,
                ProviderMetadata = CreateReByteTaskToolProviderMetadata(providerId, toolName, toolTitle, toolCallId, "tool_use")
            },
            startedAt,
            metadata);

        try
        {
            submission = await SubmitReByteTaskAsync(
                request,
                isFollowUp,
                existingState,
                submittedPayload,
                payload,
                cancellationToken);

            metadata = BuildReByteTaskMetadata(request, target, operation, submittedPayload, submission.State, null, 0, toolCallId);

            yield return CreateReByteTaskStreamEvent(
                providerId,
                toolCallId,
                "tool-output-available",
                new AIToolOutputAvailableEventData
                {
                    ToolName = submission.ToolName,
                    ProviderExecuted = true,
                    Preliminary = true,
                    Dynamic = true,
                    Output = CreateReByteTaskToolResult(submission.State, operation, null, 0),
                    ProviderMetadata = CreateReByteTaskToolProviderMetadata(providerId, submission.ToolName, submission.ToolTitle, toolCallId, "tool_result")
                },
                DateTimeOffset.UtcNow,
                metadata);

            JsonElement? donePayload = null;
            var sequence = 0;

            await foreach (var sseEvent in StreamReByteTaskEventsAsync(submission.State.TaskId!, cancellationToken))
            {
                sequence++;
                foreach (var textEvent in CreateReByteTaskRawJsonTextEvents(providerId, eventId, sequence, sseEvent, metadata))
                    yield return textEvent;

                if (string.Equals(sseEvent.Event, "done", StringComparison.OrdinalIgnoreCase))
                {
                    donePayload = ParseReByteSsePayload(sseEvent).Clone();
                    break;
                }
            }

            var finalTask = await GetReByteTaskAsync(submission.State.TaskId!, cancellationToken);
            metadata = BuildReByteTaskMetadata(request, target, operation, submittedPayload, submission.State, finalTask, 0, toolCallId);

            yield return CreateReByteTaskStreamEvent(
                providerId,
                toolCallId,
                "tool-output-available",
                new AIToolOutputAvailableEventData
                {
                    ToolName = submission.ToolName,
                    ProviderExecuted = true,
                    Preliminary = false,
                    Dynamic = true,
                    Output = CreateReByteTaskToolResult(submission.State, operation, finalTask, 0),
                    ProviderMetadata = CreateReByteTaskToolProviderMetadata(providerId, submission.ToolName, submission.ToolTitle, toolCallId, "tool_result")
                },
                DateTimeOffset.UtcNow,
                metadata);

            if (IsReByteTaskFailedStatus(GetReByteTaskStatus(finalTask)))
            {
                yield return CreateReByteTaskStreamEvent(
                    providerId,
                    eventId,
                    "error",
                    new AIErrorEventData { ErrorText = $"ReByte task {GetReByteTaskStatus(finalTask)}." },
                    DateTimeOffset.UtcNow,
                    metadata);
            }

            var response = ToReByteTaskUnifiedResponse(request, target, submission, finalTask, 0);
            yield return CreateReByteTaskFinishEvent(providerId, eventId, response, donePayload, DateTimeOffset.UtcNow);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested && !string.IsNullOrWhiteSpace(submission?.State.TaskId))
                await CancelReByteTaskSafeAsync(submission.State.TaskId!, CancellationToken.None);
        }
    }

    private async Task<ReByteTaskSubmission> SubmitReByteTaskAsync(
        AIRequest request,
        bool isFollowUp,
        ReByteTaskState existingState,
        JsonElement submittedPayload,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        if (isFollowUp)
        {
            var followUp = await SendReByteTaskFollowUpAsync(existingState.TaskId!, payload, cancellationToken);
            var promptId = TryGetString(followUp, "promptId") ?? TryGetString(followUp, "prompt_id");
            var state = existingState with
            {
                PromptId = promptId ?? existingState.PromptId,
                Raw = followUp.Clone()
            };

            return new ReByteTaskSubmission(
                "follow_up",
                ReByteFollowUpToolName,
                "Send ReByte task follow-up",
                BuildReByteTaskToolCallId("follow_up", existingState.TaskId!),
                submittedPayload,
                state);
        }

        var created = await CreateReByteTaskAsync(payload, cancellationToken);
        var taskId = TryGetString(created, "id")
                     ?? throw new InvalidOperationException("ReByte create task response did not include an id.");
        var stateCreated = new ReByteTaskState(
            taskId,
            TryGetString(created, "workspaceId") ?? TryGetString(created, "workspace_id"),
            null,
            TryGetString(created, "url"),
            created.Clone());

        return new ReByteTaskSubmission(
            "create_task",
            ReByteTaskToolName,
            "Create ReByte task",
            BuildReByteTaskToolCallId("create_task", taskId),
            submittedPayload,
            stateCreated);
    }

    private Dictionary<string, object?> BuildReByteTaskCreatePayload(
        AIRequest request,
        ReByteTaskTarget target,
        string prompt,
        string? workspaceId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["executor"] = target.Executor,
            ["model"] = target.Model,
            ["workspaceId"] = workspaceId,
            ["files"] = ResolveReByteTaskProviderOption<object>(request, "files"),
            ["skills"] = ResolveReByteTaskStringListOption(request, "skills"),
            ["githubUrl"] = ResolveReByteTaskStringOption(request, "githubUrl", "github_url"),
            ["branchName"] = ResolveReByteTaskStringOption(request, "branchName", "branch_name")
        };

        MergeReByteTaskExtraBody(request, payload);
        return PruneNullValues(payload);
    }

    private Dictionary<string, object?> BuildReByteTaskFollowUpPayload(AIRequest request, string prompt)
    {
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["skills"] = ResolveReByteTaskStringListOption(request, "skills")
        };

        var followUpExtraBody = ResolveReByteTaskProviderOption<Dictionary<string, JsonElement>>(request, "followUpExtraBody")
                                ?? ResolveReByteTaskProviderOption<Dictionary<string, JsonElement>>(request, "follow_up_extra_body");
        if (followUpExtraBody is not null)
        {
            foreach (var item in followUpExtraBody)
                payload[item.Key] = item.Value.Clone();
        }

        return PruneNullValues(payload);
    }

    private ReByteTaskTarget ResolveReByteTaskTarget(AIRequest request)
    {
        var normalized = NormalizeReByteTaskModel(request.Model);
        var explicitExecutor = ResolveReByteTaskStringOption(request, "executor");
        var explicitModel = ResolveReByteTaskStringOption(request, "model", "task_model", "executor_model");

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            var slash = normalized.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && slash < normalized.Length - 1)
            {
                var candidateExecutor = normalized[..slash];
                if (ReByteTaskExecutors.Contains(candidateExecutor))
                    return new ReByteTaskTarget(candidateExecutor, explicitModel ?? normalized[(slash + 1)..], normalized);
            }

            if (ReByteTaskExecutors.Contains(normalized))
                return new ReByteTaskTarget(explicitExecutor ?? normalized, explicitModel, normalized);

            if (!string.IsNullOrWhiteSpace(explicitExecutor))
                return new ReByteTaskTarget(explicitExecutor!, explicitModel ?? normalized, normalized);
        }

        return new ReByteTaskTarget(explicitExecutor ?? "claude", explicitModel, normalized);
    }

    private string NormalizeReByteTaskModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var trimmed = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            return trimmed[providerPrefix.Length..];

        if (trimmed.StartsWith("rebyte/", StringComparison.OrdinalIgnoreCase))
            return trimmed["rebyte/".Length..];

        return trimmed;
    }

    private static string BuildReByteTaskPrompt(AIRequest request, bool followUp)
    {
        var latestUser = ExtractLatestReByteTaskUserText(request);
        if (followUp && !string.IsNullOrWhiteSpace(latestUser))
            return latestUser!;

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            lines.Add($"Instructions:\n{request.Instructions}");

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            lines.Add(request.Input.Text!);

        foreach (var item in request.Input?.Items ?? [])
        {
            var text = ExtractReByteTaskContentText(item.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add($"{(string.IsNullOrWhiteSpace(item.Role) ? "user" : item.Role)}: {text}");
        }

        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(latestUser))
            lines.Add(latestUser!);

        return string.Join("\n\n", lines).Trim();
    }

    private static string? ExtractLatestReByteTaskUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = ExtractReByteTaskContentText(item.Content);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return request.Input?.Text ?? request.Instructions;
    }

    private static string ExtractReByteTaskContentText(IEnumerable<AIContentPart>? content)
        => string.Join("\n", (content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private ReByteTaskState ResolveReByteTaskState(AIRequest request)
    {
        var taskId = ResolveReByteTaskStringOption(request, "taskId", "task_id");
        var workspaceId = ResolveReByteTaskStringOption(request, "workspaceId", "workspace_id");
        var promptId = ResolveReByteTaskStringOption(request, "promptId", "prompt_id");

        if (!string.IsNullOrWhiteSpace(taskId) || !string.IsNullOrWhiteSpace(workspaceId))
            return new ReByteTaskState(taskId, workspaceId, promptId, null, null);

        foreach (var item in request.Input?.Items ?? [])
        {
            foreach (var toolPart in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (toolPart.ProviderExecuted != true)
                    continue;

                if (TryExtractReByteTaskState(toolPart.Output, out var state))
                    return state;

                if (TryExtractReByteTaskState(toolPart.Metadata, out state))
                    return state;

                if (TryExtractReByteTaskState(toolPart.Input, out state))
                    return state;
            }
        }

        return new ReByteTaskState(null, null, null, null, null);
    }

    private bool TryExtractReByteTaskState(object? value, out ReByteTaskState state)
    {
        state = new ReByteTaskState(null, null, null, null, null);
        if (value is null)
            return false;

        var element = value is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(value, ReByteTaskJson);

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetProperty(element, "structuredContent", out var structuredContent)
            && TryExtractReByteTaskState(structuredContent, out state))
        {
            return true;
        }

        if (TryGetProperty(element, GetIdentifier(), out var providerScoped)
            && TryExtractReByteTaskState(providerScoped, out state))
        {
            return true;
        }

        if (TryGetProperty(element, "output", out var output)
            && TryExtractReByteTaskState(output, out state))
        {
            return true;
        }

        if (TryGetProperty(element, "task", out var task)
            && TryExtractReByteTaskState(task, out state))
        {
            return true;
        }

        var taskId = TryGetString(element, "taskId") ?? TryGetString(element, "task_id");
        var workspaceId = TryGetString(element, "workspaceId") ?? TryGetString(element, "workspace_id");
        var promptId = TryGetString(element, "promptId") ?? TryGetString(element, "prompt_id");
        var url = TryGetString(element, "url");

        if (string.IsNullOrWhiteSpace(taskId)
            && (!string.IsNullOrWhiteSpace(workspaceId) || !string.IsNullOrWhiteSpace(url)))
        {
            taskId = TryGetString(element, "id");
        }

        if (string.IsNullOrWhiteSpace(taskId) && string.IsNullOrWhiteSpace(workspaceId))
            return false;

        state = new ReByteTaskState(taskId, workspaceId, promptId, url, element.Clone());
        return true;
    }

    private async Task<JsonElement> CreateReByteTaskAsync(Dictionary<string, object?> payload, CancellationToken cancellationToken)
        => await SendReByteTaskJsonAsync(HttpMethod.Post, ReByteTasksEndpoint, payload, "ReByte create task", cancellationToken);

    private async Task<JsonElement> SendReByteTaskFollowUpAsync(string taskId, Dictionary<string, object?> payload, CancellationToken cancellationToken)
        => await SendReByteTaskJsonAsync(HttpMethod.Post, $"{ReByteTasksEndpoint}/{Uri.EscapeDataString(taskId)}/prompts", payload, "ReByte task follow-up", cancellationToken);

    private async Task<JsonElement> GetReByteTaskAsync(string taskId, CancellationToken cancellationToken)
        => await SendReByteTaskJsonAsync(HttpMethod.Get, $"{ReByteTasksEndpoint}/{Uri.EscapeDataString(taskId)}", null, "ReByte get task", cancellationToken);

    private async Task<JsonElement> PollReByteTaskUntilTerminalAsync(
        string taskId,
        Action<int>? updateAttempt,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        return await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async ct =>
            {
                attempt++;
                updateAttempt?.Invoke(attempt);
                return await GetReByteTaskAsync(taskId, ct);
            },
            task => IsReByteTaskTerminalStatus(GetReByteTaskStatus(task)),
            TimeSpan.FromMilliseconds(DefaultReByteTaskPollIntervalMilliseconds),
            TimeSpan.FromSeconds(DefaultReByteTaskPollTimeoutSeconds),
            maxAttempts: null,
            cancellationToken);
    }

    private async Task CancelReByteTaskSafeAsync(string taskId, CancellationToken cancellationToken)
    {
        try
        {
            _ = await SendReByteTaskJsonAsync(HttpMethod.Post, $"{ReByteTasksEndpoint}/{Uri.EscapeDataString(taskId)}/cancel", null, "ReByte cancel task", cancellationToken);
        }
        catch
        {
            // best-effort cancellation cleanup
        }
    }

    private async Task<JsonElement> SendReByteTaskJsonAsync(
        HttpMethod method,
        string uri,
        object? payload,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, ReByteTaskJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json);
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{operation} failed ({(int)response.StatusCode}): {body}");

        if (string.IsNullOrWhiteSpace(body))
            return JsonSerializer.SerializeToElement(new { }, ReByteTaskJson);

        return JsonSerializer.Deserialize<JsonElement>(body, ReByteTaskJson).Clone();
    }

    private async IAsyncEnumerable<ReByteSseEvent> StreamReByteTaskEventsAsync(
        string taskId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ReByteTasksEndpoint}/{Uri.EscapeDataString(taskId)}/events");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"ReByte task event stream failed ({(int)response.StatusCode}): {err}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var eventName = "event";
        string? eventId = null;
        var dataLines = new List<string>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                var flushed = FlushReByteSseEvent(eventName, eventId, dataLines);
                if (flushed is not null)
                    yield return flushed;

                eventName = "event";
                eventId = null;
                dataLines.Clear();
                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            {
                eventId = line["id:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line["data:".Length..].TrimStart());
        }

        var trailing = FlushReByteSseEvent(eventName, eventId, dataLines);
        if (trailing is not null)
            yield return trailing;
    }

    private static ReByteSseEvent? FlushReByteSseEvent(string eventName, string? eventId, List<string> dataLines)
    {
        if (dataLines.Count == 0)
            return null;

        var data = string.Join("\n", dataLines);
        if (string.IsNullOrWhiteSpace(data) || data is "[DONE]" or "[done]")
            return null;

        return new ReByteSseEvent
        {
            Event = string.IsNullOrWhiteSpace(eventName) ? "event" : eventName,
            Id = eventId,
            Data = data
        };
    }

    private AIResponse ToReByteTaskUnifiedResponse(
        AIRequest request,
        ReByteTaskTarget target,
        ReByteTaskSubmission submission,
        JsonElement finalTask,
        int pollAttempt)
    {
        var metadata = BuildReByteTaskMetadata(request, target, submission.Operation, submission.Payload, submission.State, finalTask, pollAttempt, submission.ToolCallId);
        var status = GetReByteTaskStatus(finalTask);
        var rawText = BuildReByteTaskJsonCodeBlock(finalTask, "task");
        var outputItems = new List<AIOutputItem>
        {
            new()
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = submission.ToolCallId,
                        ToolName = submission.ToolName,
                        Title = submission.ToolTitle,
                        Input = submission.Payload,
                        Output = CreateReByteTaskToolResult(submission.State, submission.Operation, finalTask, pollAttempt),
                        ProviderExecuted = true,
                        State = IsReByteTaskFailedStatus(status) ? "output-error" : "output-available",
                        Metadata = new Dictionary<string, object?>
                        {
                            ["rebytetasks.task_id"] = submission.State.TaskId,
                            ["rebytetasks.workspace_id"] = GetReByteTaskWorkspaceId(finalTask) ?? submission.State.WorkspaceId,
                            ["rebytetasks.status"] = status,
                            ["rebytetasks.operation"] = submission.Operation
                        }
                    }
                ]
            },
            new()
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = rawText,
                        Metadata = new Dictionary<string, object?> { ["rebytetasks.raw"] = finalTask.Clone() }
                    }
                ]
            }
        };

        if (TryGetString(finalTask, "url") is { Length: > 0 } url)
        {
            outputItems.Add(new AIOutputItem
            {
                Type = "source-url",
                Role = "assistant",
                Metadata = new Dictionary<string, object?>
                {
                    ["source.url"] = url,
                    ["source.title"] = TryGetString(finalTask, "title") ?? "ReByte task",
                    ["rebytetasks.task_id"] = submission.State.TaskId
                }
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = BuildReByteTaskResponseModel(request, target),
            Status = ResolveReByteTaskResponseStatus(status),
            Output = new AIOutput { Items = outputItems, Metadata = metadata },
            Usage = BuildReByteTaskUsage(finalTask, pollAttempt),
            Metadata = metadata
        };
    }

    private Dictionary<string, object?> BuildReByteTaskMetadata(
        AIRequest request,
        ReByteTaskTarget target,
        string operation,
        JsonElement submittedPayload,
        ReByteTaskState state,
        JsonElement? finalTask,
        int pollAttempt,
        string? toolCallId)
    {
        var status = finalTask.HasValue ? GetReByteTaskStatus(finalTask.Value) : TryGetString(state.Raw ?? default, "status") ?? "pending";
        var taskId = finalTask.HasValue ? TryGetString(finalTask.Value, "id") ?? state.TaskId : state.TaskId;
        var workspaceId = finalTask.HasValue ? GetReByteTaskWorkspaceId(finalTask.Value) ?? state.WorkspaceId : state.WorkspaceId;
        var createdAt = finalTask.HasValue ? TryGetDateTimeOffset(finalTask.Value, "createdAt", "created_at") : null;
        var completedAt = finalTask.HasValue ? TryGetDateTimeOffset(finalTask.Value, "completedAt", "completed_at") : null;

        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["rebytetasks.operation"] = operation,
            ["rebytetasks.executor"] = target.Executor,
            ["rebytetasks.model"] = target.Model,
            ["rebytetasks.request_model"] = target.RequestModel,
            ["rebytetasks.task_id"] = taskId,
            ["rebytetasks.workspace_id"] = workspaceId,
            ["rebytetasks.prompt_id"] = state.PromptId,
            ["rebytetasks.url"] = finalTask.HasValue ? TryGetString(finalTask.Value, "url") ?? state.Url : state.Url,
            ["rebytetasks.status"] = status,
            ["rebytetasks.submitted_payload"] = submittedPayload.Clone(),
            ["rebytetasks.poll_attempt"] = pollAttempt,
            ["rebytetasks.tool_name"] = operation == "follow_up" ? ReByteFollowUpToolName : ReByteTaskToolName,
            ["rebytetasks.tool_call_id"] = toolCallId,
            ["responses.id"] = taskId ?? Guid.NewGuid().ToString("N"),
            ["responses.object"] = "response",
            ["responses.created_at"] = createdAt?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["responses.completed_at"] = completedAt?.ToUnixTimeSeconds(),
            ["responses.temperature"] = request.Temperature,
            ["responses.max_output_tokens"] = request.MaxOutputTokens,
            ["chatcompletions.response.id"] = taskId,
            ["chatcompletions.response.object"] = "chat.completion",
            ["chatcompletions.response.created"] = createdAt?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["chatcompletions.response.model"] = BuildReByteTaskResponseModel(request, target)
        };

        if (state.Raw.HasValue)
            metadata["rebytetasks.submission_raw"] = state.Raw.Value.Clone();
        if (finalTask.HasValue)
            metadata["rebytetasks.task_raw"] = finalTask.Value.Clone();
        if (IsReByteTaskFailedStatus(status))
            metadata["responses.error"] = new Responses.ResponseResultError { Code = "rebyte_task_failed", Message = $"ReByte task {status}." };

        return metadata;
    }

    private static CallToolResult CreateReByteTaskToolResult(
        ReByteTaskState state,
        string operation,
        JsonElement? finalTask,
        int pollAttempt)
    {
        var structuredContent = JsonSerializer.SerializeToElement(new
        {
            taskId = finalTask.HasValue ? TryGetString(finalTask.Value, "id") ?? state.TaskId : state.TaskId,
            task_id = finalTask.HasValue ? TryGetString(finalTask.Value, "id") ?? state.TaskId : state.TaskId,
            workspaceId = finalTask.HasValue ? GetReByteTaskWorkspaceId(finalTask.Value) ?? state.WorkspaceId : state.WorkspaceId,
            workspace_id = finalTask.HasValue ? GetReByteTaskWorkspaceId(finalTask.Value) ?? state.WorkspaceId : state.WorkspaceId,
            promptId = state.PromptId,
            prompt_id = state.PromptId,
            url = finalTask.HasValue ? TryGetString(finalTask.Value, "url") ?? state.Url : state.Url,
            status = finalTask.HasValue ? GetReByteTaskStatus(finalTask.Value) : TryGetString(state.Raw ?? default, "status"),
            operation,
            pollAttempt,
            task = finalTask?.Clone(),
            response = state.Raw?.Clone()
        }, ReByteTaskJson);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(structuredContent, ReByteTaskJson) }],
            StructuredContent = structuredContent
        };
    }

    private IEnumerable<AIStreamEvent> CreateReByteTaskRawJsonTextEvents(
        string providerId,
        string eventId,
        int sequence,
        ReByteSseEvent sseEvent,
        Dictionary<string, object?>? metadata)
    {
        var textId = $"{eventId}_sse_{sequence}";
        var timestamp = DateTimeOffset.UtcNow;
        var providerMetadata = new Dictionary<string, object>
        {
            ["event"] = sseEvent.Event,
            ["id"] = sseEvent.Id ?? string.Empty,
            ["sequence"] = sequence
        };
        var fencedJson = BuildReByteTaskSseJsonCodeBlock(sseEvent);

        yield return CreateReByteTaskStreamEvent(
            providerId,
            textId,
            "text-start",
            new AITextStartEventData { ProviderMetadata = providerMetadata },
            timestamp,
            metadata);

        yield return CreateReByteTaskStreamEvent(
            providerId,
            textId,
            "text-delta",
            new AITextDeltaEventData
            {
                Delta = fencedJson,
                ProviderMetadata = providerMetadata
            },
            timestamp,
            metadata);

        yield return CreateReByteTaskStreamEvent(
            providerId,
            textId,
            "text-end",
            new AITextEndEventData { ProviderMetadata = providerMetadata },
            timestamp,
            metadata);
    }

    private static string BuildReByteTaskSseJsonCodeBlock(ReByteSseEvent sseEvent)
    {
        var payload = ParseReByteSsePayload(sseEvent);
        var wrapper = new Dictionary<string, object?>
        {
            ["event"] = sseEvent.Event,
            ["id"] = sseEvent.Id,
            ["data"] = payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? sseEvent.Data : payload.Clone()
        };

        return BuildReByteTaskJsonCodeBlock(JsonSerializer.SerializeToElement(wrapper, ReByteTaskJson), "sse");
    }

    private static string BuildReByteTaskJsonCodeBlock(JsonElement element, string label)
    {
        var json = JsonSerializer.Serialize(element, new JsonSerializerOptions(ReByteTaskJson) { WriteIndented = true });
        return $"ReByte {label}:\n```json\n{json}\n```";
    }

    private static JsonElement ParseReByteSsePayload(ReByteSseEvent sseEvent)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(sseEvent.Data, ReByteTaskJson).Clone();
        }
        catch
        {
            return JsonSerializer.SerializeToElement(sseEvent.Data, ReByteTaskJson);
        }
    }

    private AIStreamEvent CreateReByteTaskFinishEvent(
        string providerId,
        string eventId,
        AIResponse response,
        JsonElement? donePayload,
        DateTimeOffset timestamp)
    {
        var status = response.Status ?? "completed";
        var additionalProperties = response.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                                   ?? [];
        if (donePayload.HasValue)
            additionalProperties["rebytetasks.sse_done"] = donePayload.Value.Clone();

        return CreateReByteTaskStreamEvent(
            providerId,
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = ResolveReByteTaskFinishReason(status),
                Model = response.Model,
                CompletedAt = timestamp.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model ?? "rebytetasks/claude",
                    timestamp,
                    response.Usage,
                    additionalProperties: additionalProperties)
            },
            timestamp,
            response.Metadata);
    }

    private static AIStreamEvent CreateReByteTaskStreamEvent(
        string providerId,
        string? eventId,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static Dictionary<string, Dictionary<string, object>> CreateReByteTaskToolProviderMetadata(
        string providerId,
        string toolName,
        string toolTitle,
        string toolCallId,
        string type)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = type,
                ["tool_name"] = toolName,
                ["title"] = toolTitle,
                ["tool_use_id"] = toolCallId
            }
        };

    private static string BuildReByteTaskToolCallId(string operation, string id)
        => $"rebyte_{operation}_{SanitizeReByteTaskIdentifier(id)}";

    private static string SanitizeReByteTaskIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');

        return builder.ToString().Trim('_');
    }

    private T? ResolveReByteTaskProviderOption<T>(AIRequest request, params string[] names)
    {
        if (request.Metadata is null)
            return default;

        foreach (var name in names)
        {
            var value = request.Metadata.GetProviderOption<T>(GetIdentifier(), name);
            if (!EqualityComparer<T>.Default.Equals(value, default))
                return value;

            value = request.Metadata.GetProviderOption<T>("rebyte", name);
            if (!EqualityComparer<T>.Default.Equals(value, default))
                return value;
        }

        return default;
    }

    private string? ResolveReByteTaskStringOption(AIRequest request, params string[] names)
        => ResolveReByteTaskProviderOption<string>(request, names);

    private List<string>? ResolveReByteTaskStringListOption(AIRequest request, params string[] names)
    {
        foreach (var name in names)
        {
            var list = ResolveReByteTaskProviderOption<List<string>>(request, name);
            if (list is { Count: > 0 })
                return list;

            var array = ResolveReByteTaskProviderOption<string[]>(request, name);
            if (array is { Length: > 0 })
                return array.ToList();

            var single = ResolveReByteTaskStringOption(request, name);
            if (!string.IsNullOrWhiteSpace(single))
                return [single!];
        }

        return null;
    }

    private void MergeReByteTaskExtraBody(AIRequest request, Dictionary<string, object?> payload)
    {
        var extraBody = ResolveReByteTaskProviderOption<Dictionary<string, JsonElement>>(request, "extraBody")
                        ?? ResolveReByteTaskProviderOption<Dictionary<string, JsonElement>>(request, "extra_body");
        if (extraBody is null)
            return;

        foreach (var item in extraBody)
            payload[item.Key] = item.Value.Clone();
    }

    private static Dictionary<string, object?> PruneNullValues(Dictionary<string, object?> payload)
        => payload
            .Where(static kvp => kvp.Value is not null)
            .ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);

    private static object BuildReByteTaskUsage(JsonElement finalTask, int pollAttempt)
        => new Dictionary<string, object?>
        {
            ["poll_attempts"] = pollAttempt,
            ["prompt_count"] = TryGetProperty(finalTask, "prompts", out var prompts) && prompts.ValueKind == JsonValueKind.Array
                ? prompts.GetArrayLength()
                : null
        };

    private string BuildReByteTaskResponseModel(AIRequest request, ReByteTaskTarget target)
    {
        if (!string.IsNullOrWhiteSpace(request.Model))
            return request.Model!;

        var local = string.IsNullOrWhiteSpace(target.Model)
            ? target.Executor
            : $"{target.Executor}/{target.Model}";

        return local.ToModelId(GetIdentifier());
    }

    private static string GetReByteTaskStatus(JsonElement task)
        => TryGetString(task, "status") ?? "unknown";

    private static string? GetReByteTaskWorkspaceId(JsonElement task)
        => TryGetString(task, "workspaceId") ?? TryGetString(task, "workspace_id");

    private static bool IsReByteTaskTerminalStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static bool IsReByteTaskFailedStatus(string? status)
        => string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static string ResolveReByteTaskResponseStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            ? "completed"
            : IsReByteTaskFailedStatus(status)
                ? "failed"
                : "in_progress";

    private static string ResolveReByteTaskFinishReason(string? status)
        => string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            ? "error"
            : string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
                ? "cancelled"
                : "stop";

    private static string? TryGetString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    continue;

                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null
                };
            }
        }

        return null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, params string[] propertyNames)
    {
        var value = TryGetString(element, propertyNames);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value.Clone();
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
