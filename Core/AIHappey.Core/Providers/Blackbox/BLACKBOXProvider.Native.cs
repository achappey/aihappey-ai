using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BLACKBOX;

public partial class BLACKBOXProvider
{
    private const string NativeCloudBase = "https://cloud.blackbox.ai/";
    private const string NativeTasksPath = "api/tasks";

    private static readonly JsonSerializerOptions NativeJson = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private sealed class BlackboxNativeTaskCreateRequest
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; init; } = default!;

        [JsonPropertyName("selectedAgent")]
        public string SelectedAgent { get; init; } = default!;

        [JsonPropertyName("selectedModel")]
        public string SelectedModel { get; init; } = default!;
    }

    private sealed class BlackboxNativeTaskCreateResult
    {
        public string TaskId { get; init; } = default!;
        public string? TaskUrl { get; init; }
    }

    private sealed class BlackboxNativeTaskStatus
    {
        public string TaskId { get; init; } = default!;
        public string Status { get; init; } = default!;
        public double? Progress { get; init; }
        public bool? InProgress { get; init; }
        public bool? IsDone { get; init; }
        public string? Error { get; init; }
        public string? CreatedAt { get; init; }
        public string? UpdatedAt { get; init; }
        public string? CompletedAt { get; init; }
        public string? Duration { get; init; }
    }

    private sealed class BlackboxNativeTaskView
    {
        public string Id { get; init; } = default!;
        public string Status { get; init; } = default!;
        public string? Error { get; init; }
        public string? SelectedAgent { get; init; }
        public string? SelectedModel { get; init; }
        public string? Prompt { get; init; }
        public string? CreatedAt { get; init; }
        public string? UpdatedAt { get; init; }
        public string? CompletedAt { get; init; }
        public JsonElement Root { get; init; }
    }

    private sealed class BlackboxNativeTerminalResult
    {
        public BlackboxNativeTaskCreateResult Created { get; init; } = default!;
        public BlackboxNativeTaskStatus Status { get; init; } = default!;
        public BlackboxNativeTaskView Task { get; init; } = default!;
        public string OutputText { get; init; } = string.Empty;
    }

    private sealed class BlackboxNativeSseEvent
    {
        public string EventType { get; init; } = default!;
        public string RawData { get; init; } = string.Empty;
        public JsonElement? Data { get; init; }
    }

    private static bool IsNativeAgentModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var trimmed = model.Trim();
        return trimmed.StartsWith("blackbox/agent/", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("agent/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveNativeAgent(string? model, out string selectedAgent, out string selectedModel)
    {
        selectedAgent = string.Empty;
        selectedModel = string.Empty;

        if (!IsNativeAgentModel(model))
            return false;

        var local = model!.Trim();
        if (local.StartsWith("blackbox/", StringComparison.OrdinalIgnoreCase))
            local = local["blackbox/".Length..];

        if (!local.StartsWith("agent/", StringComparison.OrdinalIgnoreCase))
            return false;

        var agentAndVariant = local["agent/".Length..].Trim();
        if (string.IsNullOrWhiteSpace(agentAndVariant))
            return false;

        var firstSeparator = agentAndVariant.IndexOf('/');
        if (firstSeparator <= 0 || firstSeparator >= agentAndVariant.Length - 1)
            return false;

        var agent = agentAndVariant[..firstSeparator].Trim();
        var variant = agentAndVariant[(firstSeparator + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(agent)
            || string.IsNullOrWhiteSpace(variant)
            || variant.Contains('/', StringComparison.Ordinal))
            return false;

        selectedAgent = agent.ToLowerInvariant();
        selectedModel = variant;
        return true;
    }

    private static string BuildUnsupportedNativeAgentModelMessage(string? model)
        => $"Unsupported BLACKBOX native agent model '{model}'. Expected format: 'blackbox/agent/{{agent}}/{{variant}}' (or 'agent/{{agent}}/{{variant}}'). Legacy IDs like 'blackbox/agent/{{agent}}' are not supported.";

    private static string BuildNativeTaskPromptFromUi(IEnumerable<UIMessage>? messages)
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

    private static string BuildNativeTaskPromptFromCompletionMessages(IEnumerable<ChatMessage>? messages)
    {
        var lines = new List<string>();
        foreach (var message in messages ?? [])
        {
            var text = ChatMessageContentExtensions.ToText(message.Content) ?? message.Content.GetRawText();
            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{message.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildNativeTaskPromptFromResponseRequest(ResponseRequest request)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            lines.Add($"system: {request.Instructions}");

        if (request.Input?.IsText == true && !string.IsNullOrWhiteSpace(request.Input.Text))
            lines.Add($"user: {request.Input.Text}");

        if (request.Input?.IsItems == true)
        {
            foreach (var item in request.Input.Items ?? [])
            {
                if (item is not ResponseInputMessage message)
                    continue;

                var text = message.Content.IsText
                    ? message.Content.Text
                    : string.Join("\n", message.Content.Parts?
                        .OfType<InputTextPart>()
                        .Select(p => p.Text)
                        .Where(t => !string.IsNullOrWhiteSpace(t)) ?? []);

                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add($"{message.Role.ToString().ToLowerInvariant()}: {text}");
            }
        }

        return string.Join("\n\n", lines);
    }

    private static bool IsTerminalTaskStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "timeout", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompletedTaskStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    private static long ToUnixTimeOrNow(string? dateTime)
        => DateTimeOffset.TryParse(dateTime, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private async Task<BlackboxNativeTaskCreateResult> CreateNativeTaskAsync(
        BlackboxNativeTaskCreateRequest request,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(request, NativeJson);

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(NativeCloudBase), NativeTasksPath))
        {
            Content = new StringContent(payload, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"BLACKBOX create task failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var taskRoot = root.TryGetProperty("task", out var t) && t.ValueKind == JsonValueKind.Object
            ? t
            : root;

        var id = taskRoot.TryGetProperty("id", out var idEl)
            ? idEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("BLACKBOX create task response did not contain task id.");

        return new BlackboxNativeTaskCreateResult
        {
            TaskId = id!,
            TaskUrl = root.TryGetProperty("taskUrl", out var taskUrlEl) && taskUrlEl.ValueKind == JsonValueKind.String
                ? taskUrlEl.GetString()
                : null
        };
    }

    private async Task<BlackboxNativeTaskStatus> GetNativeTaskStatusAsync(string taskId, CancellationToken cancellationToken)
    {
        var uri = new Uri(new Uri(NativeCloudBase), $"{NativeTasksPath}/{Uri.EscapeDataString(taskId)}/status");
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"BLACKBOX task status failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString() ?? "unknown"
            : "unknown";

        return new BlackboxNativeTaskStatus
        {
            TaskId = root.TryGetProperty("taskId", out var taskIdEl) && taskIdEl.ValueKind == JsonValueKind.String
                ? taskIdEl.GetString() ?? taskId
                : taskId,
            Status = status,
            Progress = root.TryGetProperty("progress", out var progressEl) && progressEl.ValueKind == JsonValueKind.Number
                ? progressEl.GetDouble()
                : null,
            InProgress = root.TryGetProperty("inProgress", out var inProgressEl)
                         && (inProgressEl.ValueKind == JsonValueKind.True || inProgressEl.ValueKind == JsonValueKind.False)
                ? inProgressEl.GetBoolean()
                : null,
            IsDone = root.TryGetProperty("isDone", out var isDoneEl)
                     && (isDoneEl.ValueKind == JsonValueKind.True || isDoneEl.ValueKind == JsonValueKind.False)
                ? isDoneEl.GetBoolean()
                : null,
            Error = root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String
                ? errorEl.GetString()
                : null,
            CreatedAt = root.TryGetProperty("createdAt", out var createdAtEl) && createdAtEl.ValueKind == JsonValueKind.String
                ? createdAtEl.GetString()
                : null,
            UpdatedAt = root.TryGetProperty("updatedAt", out var updatedAtEl) && updatedAtEl.ValueKind == JsonValueKind.String
                ? updatedAtEl.GetString()
                : null,
            CompletedAt = root.TryGetProperty("completedAt", out var completedAtEl) && completedAtEl.ValueKind == JsonValueKind.String
                ? completedAtEl.GetString()
                : null,
            Duration = root.TryGetProperty("duration", out var durationEl) && durationEl.ValueKind == JsonValueKind.String
                ? durationEl.GetString()
                : null
        };
    }

    private async Task<BlackboxNativeTaskView> GetNativeTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        var uri = new Uri(new Uri(NativeCloudBase), $"{NativeTasksPath}/{Uri.EscapeDataString(taskId)}");
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"BLACKBOX get task failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var taskRoot = root.TryGetProperty("task", out var taskEl) && taskEl.ValueKind == JsonValueKind.Object
            ? taskEl
            : root;

        var id = taskRoot.TryGetProperty("id", out var idEl)
                 && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : taskId;

        return new BlackboxNativeTaskView
        {
            Id = id ?? taskId,
            Status = taskRoot.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString() ?? "unknown"
                : "unknown",
            Error = taskRoot.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String
                ? errorEl.GetString()
                : null,
            SelectedAgent = taskRoot.TryGetProperty("selectedAgent", out var selectedAgentEl) && selectedAgentEl.ValueKind == JsonValueKind.String
                ? selectedAgentEl.GetString()
                : null,
            SelectedModel = taskRoot.TryGetProperty("selectedModel", out var selectedModelEl) && selectedModelEl.ValueKind == JsonValueKind.String
                ? selectedModelEl.GetString()
                : null,
            Prompt = taskRoot.TryGetProperty("prompt", out var promptEl) && promptEl.ValueKind == JsonValueKind.String
                ? promptEl.GetString()
                : null,
            CreatedAt = taskRoot.TryGetProperty("createdAt", out var createdAtEl) && createdAtEl.ValueKind == JsonValueKind.String
                ? createdAtEl.GetString()
                : null,
            UpdatedAt = taskRoot.TryGetProperty("updatedAt", out var updatedAtEl) && updatedAtEl.ValueKind == JsonValueKind.String
                ? updatedAtEl.GetString()
                : null,
            CompletedAt = taskRoot.TryGetProperty("completedAt", out var completedAtEl) && completedAtEl.ValueKind == JsonValueKind.String
                ? completedAtEl.GetString()
                : null,
            Root = taskRoot.Clone()
        };
    }

    private async Task StopNativeTaskSafeAsync(string taskId, CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(new Uri(NativeCloudBase), $"{NativeTasksPath}/{Uri.EscapeDataString(taskId)}");
            using var req = new HttpRequestMessage(HttpMethod.Patch, uri)
            {
                Content = new StringContent("{\"action\":\"stop\"}", Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            _ = await resp.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            // best effort
        }
    }

    private static string ExtractFinalTextFromTask(BlackboxNativeTaskView task)
    {
        var fromFollowup = ExtractFollowupText(task.Root);
        if (!string.IsNullOrWhiteSpace(fromFollowup))
            return fromFollowup!;

        var fromLogs = ExtractLogText(task.Root);
        if (!string.IsNullOrWhiteSpace(fromLogs))
            return fromLogs!;

        if (!string.IsNullOrWhiteSpace(task.Error))
            return task.Error!;

        return string.Empty;
    }

    private static string? ExtractFollowupText(JsonElement taskRoot)
    {
        if (!taskRoot.TryGetProperty("followupMessages", out var followup)
            || followup.ValueKind != JsonValueKind.Array)
            return null;

        var buffer = new List<string>();
        foreach (var item in followup.EnumerateArray())
            CollectText(item, buffer, maxDepth: 5);

        var clean = buffer
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToArray();

        return clean.Length == 0 ? null : string.Join("\n\n", clean);
    }

    private static string? ExtractLogText(JsonElement taskRoot)
    {
        if (!taskRoot.TryGetProperty("logs", out var logs)
            || logs.ValueKind != JsonValueKind.Array)
            return null;

        var agentResponse = new List<string>();
        var all = new List<string>();

        foreach (var log in logs.EnumerateArray())
        {
            if (log.ValueKind != JsonValueKind.Object)
                continue;

            var message = log.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(message))
                continue;

            var trimmed = message!.Trim();
            all.Add(trimmed);

            var contentType = log.TryGetProperty("contentType", out var contentTypeEl) && contentTypeEl.ValueKind == JsonValueKind.String
                ? contentTypeEl.GetString()
                : null;

            if (string.Equals(contentType, "agentResponse", StringComparison.OrdinalIgnoreCase))
                agentResponse.Add(trimmed);
        }

        if (agentResponse.Count > 0)
            return string.Join("\n\n", agentResponse);

        return all.Count > 0 ? string.Join("\n", all) : null;
    }

    private static void CollectText(JsonElement element, List<string> buffer, int maxDepth)
    {
        if (maxDepth <= 0)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                {
                    var text = element.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        buffer.Add(text!);
                    return;
                }
            case JsonValueKind.Array:
                {
                    foreach (var item in element.EnumerateArray())
                        CollectText(item, buffer, maxDepth - 1);
                    return;
                }
            case JsonValueKind.Object:
                {
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String
                            && (string.Equals(prop.Name, "text", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(prop.Name, "message", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(prop.Name, "content", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(prop.Name, "output", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(prop.Name, "response", StringComparison.OrdinalIgnoreCase)))
                        {
                            var text = prop.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(text))
                                buffer.Add(text!);
                        }
                        else
                        {
                            CollectText(prop.Value, buffer, maxDepth - 1);
                        }
                    }
                    return;
                }
        }
    }

    private async Task<BlackboxNativeTerminalResult> ExecuteNativeAgentTaskAsync(
        string model,
        string prompt,
        CancellationToken cancellationToken)
    {
        if (!TryResolveNativeAgent(model, out var selectedAgent, out var selectedModel))
            throw new NotSupportedException(BuildUnsupportedNativeAgentModelMessage(model));

        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("BLACKBOX native agent requires a non-empty prompt.");

        var created = await CreateNativeTaskAsync(new BlackboxNativeTaskCreateRequest
        {
            Prompt = prompt,
            SelectedAgent = selectedAgent,
            SelectedModel = selectedModel
        }, cancellationToken);

        try
        {
            var finalStatus = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
                poll: ct => GetNativeTaskStatusAsync(created.TaskId, ct),
                isTerminal: s => IsTerminalTaskStatus(s.Status),
                interval: TimeSpan.FromSeconds(2),
                timeout: TimeSpan.FromMinutes(20),
                maxAttempts: null,
                cancellationToken: cancellationToken);

            var task = await GetNativeTaskAsync(created.TaskId, cancellationToken);
            var outputText = ExtractFinalTextFromTask(task);

            return new BlackboxNativeTerminalResult
            {
                Created = created,
                Status = finalStatus,
                Task = task,
                OutputText = outputText
            };
        }
        catch (OperationCanceledException)
        {
            await StopNativeTaskSafeAsync(created.TaskId, CancellationToken.None);
            throw;
        }
    }

    private async IAsyncEnumerable<BlackboxNativeSseEvent> StreamNativeTaskEventsAsync(
        string taskId,
        int fromIndex,
        bool includeStatus,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var uri = new Uri(new Uri(NativeCloudBase),
            $"{NativeTasksPath}/{Uri.EscapeDataString(taskId)}/stream?fromIndex={fromIndex}&includeStatus={(includeStatus ? "true" : "false")}");

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var contentType = resp.Content.Headers.ContentType?.MediaType;

        if (!resp.IsSuccessStatusCode || !string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"BLACKBOX task stream failed ({(int)resp.StatusCode}): {raw}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? currentEvent = null;
        var dataBuilder = new StringBuilder();

        async IAsyncEnumerable<BlackboxNativeSseEvent> FlushCurrentEventAsync()
        {
            if (dataBuilder.Length == 0)
                yield break;

            var data = dataBuilder.ToString().TrimEnd('\r', '\n');
            dataBuilder.Clear();

            var evtType = string.IsNullOrWhiteSpace(currentEvent) ? "message" : currentEvent!;
            currentEvent = null;

            JsonElement? parsed = null;
            if (!string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    parsed = doc.RootElement.Clone();
                }
                catch
                {
                    // keep raw only
                }
            }

            yield return new BlackboxNativeSseEvent
            {
                EventType = evtType,
                RawData = data,
                Data = parsed
            };

            await Task.CompletedTask;
        }

        string? line;
        while (!cancellationToken.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (line.Length == 0)
            {
                await foreach (var flushed in FlushCurrentEventAsync())
                    yield return flushed;

                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataBuilder.AppendLine(line["data:".Length..].Trim());
            }
        }

        await foreach (var flushed in FlushCurrentEventAsync())
            yield return flushed;
    }

    private static bool TryExtractLogPayload(JsonElement data, out int index, out string? message, out string? logType, out string? contentType, out string? step, out string? agent)
    {
        index = -1;
        message = null;
        logType = null;
        contentType = null;
        step = null;
        agent = null;

        if (data.ValueKind != JsonValueKind.Object)
            return false;

        if (data.TryGetProperty("index", out var indexEl) && indexEl.ValueKind == JsonValueKind.Number)
            index = indexEl.GetInt32();

        if (!data.TryGetProperty("log", out var logEl) || logEl.ValueKind != JsonValueKind.Object)
            return false;

        message = logEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
            ? msgEl.GetString()
            : null;
        logType = logEl.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString()
            : null;
        contentType = logEl.TryGetProperty("contentType", out var contentTypeEl) && contentTypeEl.ValueKind == JsonValueKind.String
            ? contentTypeEl.GetString()
            : null;
        step = logEl.TryGetProperty("step", out var stepEl) && stepEl.ValueKind == JsonValueKind.String
            ? stepEl.GetString()
            : null;
        agent = logEl.TryGetProperty("agent", out var agentEl) && agentEl.ValueKind == JsonValueKind.String
            ? agentEl.GetString()
            : null;

        return true;
    }

    private static bool TryExtractStatusPayload(JsonElement data, out string status, out string? error)
    {
        status = "unknown";
        error = null;

        if (data.ValueKind != JsonValueKind.Object)
            return false;

        if (data.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
            status = statusEl.GetString() ?? "unknown";

        if (data.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
            error = errorEl.GetString();

        return true;
    }

    private static string GetStatusFinishReason(string status)
        => IsCompletedTaskStatus(status) ? "stop" : "error";

    private static string GetStatusErrorCode(string status)
        => $"blackbox_task_{status.ToLowerInvariant()}";

    private static Dictionary<string, object?> MergeTaskMetadata(
        Dictionary<string, object?>? metadata,
        BlackboxNativeTaskView task,
        BlackboxNativeTaskStatus status)
    {
        var merged = metadata is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(metadata);

        merged["blackbox_task_id"] = task.Id;
        merged["blackbox_task_status"] = status.Status;
        merged["blackbox_selected_agent"] = task.SelectedAgent;
        merged["blackbox_selected_model"] = task.SelectedModel;

        return merged;
    }

    private ResponseResult ToNativeResponseResult(BlackboxNativeTerminalResult terminal, ResponseRequest request)
    {
        var status = terminal.Status.Status;
        var isCompleted = IsCompletedTaskStatus(status);
        var model = request.Model ?? terminal.Task.SelectedModel ?? "blackbox-agent";

        return new ResponseResult
        {
            Id = terminal.Task.Id,
            Object = "response",
            CreatedAt = ToUnixTimeOrNow(terminal.Task.CreatedAt),
            CompletedAt = ToUnixTimeOrNow(terminal.Task.CompletedAt ?? terminal.Status.CompletedAt),
            Status = isCompleted ? "completed" : "failed",
            Model = model,
            Temperature = request.Temperature,
            Metadata = MergeTaskMetadata(request.Metadata, terminal.Task, terminal.Status),
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            ParallelToolCalls = request.ParallelToolCalls,
            Usage = new
            {
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0
            },
            Error = isCompleted
                ? null
                : new ResponseResultError
                {
                    Code = GetStatusErrorCode(status),
                    Message = terminal.Task.Error
                              ?? terminal.Status.Error
                              ?? "BLACKBOX task did not complete successfully."
                },
            Output =
            [
                new
                {
                    id = $"msg_{terminal.Task.Id}",
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = terminal.OutputText
                        }
                    }
                }
            ]
        };
    }

   
}
