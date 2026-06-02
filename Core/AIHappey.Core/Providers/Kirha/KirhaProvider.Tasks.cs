using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Kirha;

public partial class KirhaProvider
{
    private const string KirhaTasksModelId = "kirha/kirha-tasks";
    private const string KirhaTaskToolName = "kirha_task";
    private static readonly TimeSpan KirhaTaskPollingInterval = TimeSpan.FromSeconds(2);

    private static bool IsKirhaTasksModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var normalized = model.Trim();
        return string.Equals(normalized, KirhaTasksModelId, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "kirha-tasks", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AIResponse> ExecuteKirhaTaskUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var context = CreateKirhaTaskRequestContext(request);
        var result = await ExecuteKirhaTaskAsync(context, cancellationToken);
        return CreateUnifiedTaskResponse(request, result);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamKirhaTaskUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var providerId = GetIdentifier();
        var eventId = request.Id ?? Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        var toolCallId = $"kirha-task-{eventId}";

        KirhaTaskRequestContext? context = null;
        Exception? setupError = null;
        try
        {
            ApplyAuthHeader();
            context = CreateKirhaTaskRequestContext(request);
        }
        catch (Exception ex)
        {
            setupError = ex;
        }

        if (setupError is not null)
        {
            yield return CreateKirhaTaskErrorEvent(providerId, eventId, setupError, timestamp);
            yield break;
        }

        var inputMetadata = CreateTaskToolMetadata(context, null, "input-available", completed: false);
        yield return CreateStreamEvent(providerId, toolCallId, "tool-input-available",
            new AIToolInputAvailableEventData
            {
                ToolName = KirhaTaskToolName,
                Title = "Create Kirha research task",
                Input = context.Payload,
                ProviderExecuted = true,
                ProviderMetadata = ToProviderMetadataEnvelope(inputMetadata, providerId)
            }, timestamp, inputMetadata);

        KirhaTaskCreateResponse? created = null;
        Exception? createError = null;
        try
        {
            created = await CreateKirhaTaskAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            createError = ex;
        }

        if (createError is not null)
        {
            yield return CreateKirhaTaskErrorEvent(providerId, eventId, createError, timestamp);
            yield break;
        }

        var createdMetadata = CreateTaskToolMetadata(context, created.Id, created.Status, completed: IsTerminalTaskStatus(created.Status));
        yield return CreateStreamEvent(providerId, toolCallId, "tool-output-available",
            new AIToolOutputAvailableEventData
            {
                ToolName = KirhaTaskToolName,
                Output = CreateKirhaToolResult(new Dictionary<string, object?>
                {
                    ["provider"] = "kirha",
                    ["operation"] = "task_created",
                    ["task_id"] = created.Id,
                    ["status"] = created.Status,
                    ["response"] = created
                }),
                ProviderExecuted = true,
                Preliminary = true,
                ProviderMetadata = ToProviderMetadataEnvelope(createdMetadata, providerId)
            }, timestamp, createdMetadata);

        KirhaTaskResponse? current = null;
        while (true)
        {
            Exception? pollError = null;
            KirhaTaskResponse? polled = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(KirhaTaskPollingInterval, cancellationToken);

                polled = await GetKirhaTaskAsync(created.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                pollError = ex;
            }

            if (pollError is not null)
            {
                yield return CreateKirhaTaskErrorEvent(providerId, eventId, pollError, timestamp);
                yield break;
            }

            current = polled;
            if (current is null)
                yield break;

            var terminal = IsTerminalTaskStatus(current.Status);
            var pollMetadata = CreateTaskToolMetadata(context, current.Id ?? created.Id, current.Status, completed: terminal);

            yield return CreateStreamEvent(providerId, toolCallId, "tool-output-available",
                new AIToolOutputAvailableEventData
                {
                    ToolName = KirhaTaskToolName,
                    Output = CreateKirhaToolResult(new Dictionary<string, object?>
                    {
                        ["provider"] = "kirha",
                        ["operation"] = terminal ? "task_result" : "task_status",
                        ["task_id"] = current.Id ?? created.Id,
                        ["status"] = current.Status,
                        ["result"] = NormalizeTaskJsonElement(current.Result),
                        ["error"] = NormalizeTaskJsonElement(current.Error),
                        ["response"] = current
                    }),
                    ProviderExecuted = true,
                    Preliminary = !terminal,
                    ProviderMetadata = ToProviderMetadataEnvelope(pollMetadata, providerId)
                }, timestamp, pollMetadata);

            if (terminal)
                break;
        }

        if (current is null)
            yield break;

        var result = new KirhaTaskResult
        {
            Context = context,
            Created = created,
            Task = current,
            ResultText = ExtractTaskResultText(current),
            ErrorText = ExtractTaskErrorText(current),
            Metadata = BuildKirhaTaskMetadata(context, current)
        };

        var response = CreateUnifiedTaskResponse(request, result);
        if (!IsSuccessTaskStatus(current.Status))
        {
            yield return CreateStreamEvent(providerId, toolCallId, "tool-output-error",
                new AIToolOutputErrorEventData
                {
                    ToolCallId = toolCallId,
                    ErrorText = result.ErrorText ?? $"Kirha task '{current.Id ?? created.Id}' failed with status '{current.Status ?? "unknown"}'.",
                    ProviderExecuted = true,
                    ProviderMetadata = ToProviderMetadataEnvelope(response.Metadata, providerId)
                }, timestamp, response.Metadata);

            yield return CreateStreamEvent(providerId, eventId, "error",
                new AIErrorEventData
                {
                    ErrorText = result.ErrorText ?? $"Kirha task '{current.Id ?? created.Id}' failed with status '{current.Status ?? "unknown"}'."
                }, timestamp, response.Metadata);
        }

        if (!string.IsNullOrWhiteSpace(result.ResultText))
        {
            var textId = $"{eventId}:text";
            var textMetadata = CreateTaskTextMetadata(result);

            yield return CreateStreamEvent(providerId, textId, "text-start", new AITextStartEventData(), timestamp, textMetadata);
            foreach (var chunk in ChunkTaskText(result.ResultText))
            {
                yield return CreateStreamEvent(providerId, textId, "text-delta",
                    new AITextDeltaEventData { Delta = chunk }, timestamp, textMetadata);
            }

            yield return CreateStreamEvent(providerId, textId, "text-end", new AITextEndEventData(), timestamp, textMetadata);
        }

        yield return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = eventId,
                Timestamp = timestamp,
                Output = response.Output,
                Data = new AIFinishEventData
                {
                    FinishReason = response.Status == "failed" ? "error" : "stop",
                    Model = response.Model,
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    InputTokens = 0,
                    OutputTokens = 0,
                    TotalTokens = 0,
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        response.Model ?? request.Model ?? KirhaTasksModelId,
                        timestamp,
                        response.Usage,
                        outputTokens: 0,
                        inputTokens: 0,
                        totalTokens: 0,
                        temperature: request.Temperature,
                        additionalProperties: response.Metadata)
                }
            },
            Metadata = response.Metadata
        };
    }

    private async Task<KirhaTaskResult> ExecuteKirhaTaskAsync(KirhaTaskRequestContext context, CancellationToken cancellationToken)
    {
        var created = await CreateKirhaTaskAsync(context, cancellationToken);
        var task = await PollKirhaTaskUntilTerminalAsync(created.Id, cancellationToken);

        return new KirhaTaskResult
        {
            Context = context,
            Created = created,
            Task = task,
            ResultText = ExtractTaskResultText(task),
            ErrorText = ExtractTaskErrorText(task),
            Metadata = BuildKirhaTaskMetadata(context, task)
        };
    }

    private async Task<KirhaTaskCreateResponse> CreateKirhaTaskAsync(KirhaTaskRequestContext context, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(context.Payload, Json);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/tasks")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Kirha task create failed ({(int)resp.StatusCode}): {body}");

        var dto = JsonSerializer.Deserialize<KirhaTaskCreateResponse>(body, Json)
                  ?? throw new InvalidOperationException("Kirha task create returned an empty response.");

        if (string.IsNullOrWhiteSpace(dto.Id))
            throw new InvalidOperationException($"Kirha task create returned no task id: {body}");

        return dto;
    }

    private async Task<KirhaTaskResponse> PollKirhaTaskUntilTerminalAsync(string taskId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var task = await GetKirhaTaskAsync(taskId, cancellationToken);
            if (IsTerminalTaskStatus(task.Status))
                return task;

            await Task.Delay(KirhaTaskPollingInterval, cancellationToken);
        }
    }

    private async Task<KirhaTaskResponse> GetKirhaTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"v1/tasks/{Uri.EscapeDataString(taskId)}");
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Kirha task retrieve failed ({(int)resp.StatusCode}): {body}");

        return JsonSerializer.Deserialize<KirhaTaskResponse>(body, Json)
               ?? throw new InvalidOperationException("Kirha task retrieve returned an empty response.");
    }

    private KirhaTaskRequestContext CreateKirhaTaskRequestContext(AIRequest request)
    {
        var providerOptions = GetUnifiedProviderOptions(request);
        var query = ExtractLatestUserText(request);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Kirha tasks require text parts from the last user message.");

        var instruction = ReadString(providerOptions, "instruction")
                          ?? ReadString(providerOptions, "instructions")
                          ?? request.Instructions;

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["query"] = query
        };

        if (!string.IsNullOrWhiteSpace(instruction))
            payload["instruction"] = instruction;

        return new KirhaTaskRequestContext
        {
            Query = query,
            Instruction = instruction,
            Payload = payload,
            ProviderOptions = providerOptions
        };
    }

    private AIResponse CreateUnifiedTaskResponse(AIRequest request, KirhaTaskResult result)
    {
        var taskId = result.Task.Id ?? result.Created.Id;
        var success = IsSuccessTaskStatus(result.Task.Status);
        var status = success ? "completed" : "failed";
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
                        ToolCallId = $"kirha-task-{TaskIdSuffix(taskId)}",
                        ToolName = KirhaTaskToolName,
                        Title = "Kirha research task",
                        Input = result.Context.Payload,
                        Output = CreateKirhaToolResult(new Dictionary<string, object?>
                        {
                            ["provider"] = "kirha",
                            ["operation"] = "task",
                            ["task_id"] = taskId,
                            ["status"] = result.Task.Status,
                            ["query"] = result.Task.Query ?? result.Context.Query,
                            ["instruction"] = result.Task.Instruction ?? result.Context.Instruction,
                            ["result"] = NormalizeTaskJsonElement(result.Task.Result),
                            ["error"] = NormalizeTaskJsonElement(result.Task.Error),
                            ["createdAt"] = result.Task.CreatedAt,
                            ["updatedAt"] = result.Task.UpdatedAt,
                            ["response"] = result.Task
                        }),
                        ProviderExecuted = true,
                        State = success ? "output-available" : "output-error",
                        Metadata = CreateTaskToolMetadata(result.Context, taskId, result.Task.Status, completed: true)
                    }
                ]
            }
        };

        var text = success ? result.ResultText : result.ErrorText;
        if (!string.IsNullOrWhiteSpace(text))
        {
            outputItems.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = text!,
                        Metadata = CreateTaskTextMetadata(result)
                    }
                ]
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = status,
            Output = new AIOutput
            {
                Items = outputItems,
                Metadata = result.Metadata
            },
            Usage = BuildKirhaTaskUsage(),
            Metadata = result.Metadata
        };
    }

    private static string ExtractTaskResultText(KirhaTaskResponse task)
    {
        if (task.Result is null)
            return string.Empty;

        var result = task.Result.Value;
        return result.ValueKind switch
        {
            JsonValueKind.String => result.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => result.GetRawText()
        };
    }

    private static string? ExtractTaskErrorText(KirhaTaskResponse task)
    {
        if (task.Error is null)
            return null;

        var error = task.Error.Value;
        if (error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (error.ValueKind == JsonValueKind.String)
            return error.GetString();

        if (error.ValueKind == JsonValueKind.Object)
        {
            var message = TryGetString(error, "message", "error", "detail", "reason");
            if (!string.IsNullOrWhiteSpace(message))
                return message;
        }

        return error.GetRawText();
    }

    private static Dictionary<string, object?> BuildKirhaTaskMetadata(KirhaTaskRequestContext context, KirhaTaskResponse task)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = task.Id,
            ["status"] = task.Status,
            ["query"] = task.Query ?? context.Query,
            ["instruction"] = task.Instruction ?? context.Instruction,
            ["result"] = NormalizeTaskJsonElement(task.Result),
            ["error"] = NormalizeTaskJsonElement(task.Error),
            ["createdAt"] = task.CreatedAt,
            ["updatedAt"] = task.UpdatedAt,
            ["kirha.operation"] = "task",
            ["kirha.endpoint"] = "v1/tasks",
            ["kirha.task_id"] = task.Id,
            ["kirha.completed"] = IsTerminalTaskStatus(task.Status),
            ["kirha.success"] = IsSuccessTaskStatus(task.Status)
        };

    private static Dictionary<string, object?> CreateTaskToolMetadata(
        KirhaTaskRequestContext context,
        string? taskId,
        string? status,
        bool completed)
        => new()
        {
            ["kirha"] = new Dictionary<string, object?>
            {
                ["operation"] = "task",
                ["endpoint"] = "v1/tasks",
                ["task_id"] = taskId,
                ["completed"] = completed,
                ["tool_name"] = KirhaTaskToolName,
                ["status"] = status,
                ["query"] = context.Query,
                ["instruction"] = context.Instruction
            }
        };

    private static Dictionary<string, object?> CreateTaskTextMetadata(KirhaTaskResult result)
        => new()
        {
            ["kirha"] = new Dictionary<string, object?>
            {
                ["operation"] = "task",
                ["endpoint"] = "v1/tasks",
                ["task_id"] = result.Task.Id ?? result.Created.Id,
                ["completed"] = true,
                ["status"] = result.Task.Status,
                ["success"] = IsSuccessTaskStatus(result.Task.Status)
            }
        };

    private static AIStreamEvent CreateKirhaTaskErrorEvent(
        string providerId,
        string eventId,
        Exception error,
        DateTimeOffset timestamp)
        => CreateStreamEvent(providerId, eventId, "error",
            new AIErrorEventData { ErrorText = error.Message },
            timestamp,
            new Dictionary<string, object?>
            {
                ["kirha.error.type"] = error.GetType().FullName
            });

    private static object BuildKirhaTaskUsage()
        => new Dictionary<string, object?>
        {
            ["prompt_tokens"] = 0,
            ["completion_tokens"] = 0,
            ["total_tokens"] = 0,
            ["inputTokens"] = 0,
            ["outputTokens"] = 0,
            ["totalTokens"] = 0
        };

    private static object? NormalizeTaskJsonElement(JsonElement? element)
    {
        if (element is null)
            return null;

        return element.Value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.Value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.Value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.Value.TryGetDouble(out var doubleValue) => doubleValue,
            _ => JsonSerializer.Deserialize<object>(element.Value.GetRawText(), Json)
        };
    }

    private static bool IsTerminalTaskStatus(string? status)
        => status is not null
           && (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
               || status.Equals("success", StringComparison.OrdinalIgnoreCase)
               || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("error", StringComparison.OrdinalIgnoreCase)
               || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
               || status.Equals("canceled", StringComparison.OrdinalIgnoreCase));

    private static bool IsSuccessTaskStatus(string? status)
        => status is not null
           && (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
               || status.Equals("success", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ChunkTaskText(string text, int chunkSize = 180)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        for (var i = 0; i < text.Length; i += chunkSize)
            yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
    }

    private static string TaskIdSuffix(string? taskId)
        => string.IsNullOrWhiteSpace(taskId) ? Guid.NewGuid().ToString("N") : taskId;

    private sealed class KirhaTaskRequestContext
    {
        public string Query { get; init; } = string.Empty;

        public string? Instruction { get; init; }

        public Dictionary<string, object?> Payload { get; init; } = [];

        public Dictionary<string, object?> ProviderOptions { get; init; } = [];
    }

    private sealed class KirhaTaskResult
    {
        public KirhaTaskRequestContext Context { get; init; } = default!;

        public KirhaTaskCreateResponse Created { get; init; } = default!;

        public KirhaTaskResponse Task { get; init; } = default!;

        public string ResultText { get; init; } = string.Empty;

        public string? ErrorText { get; init; }

        public Dictionary<string, object?> Metadata { get; init; } = [];
    }

    private sealed class KirhaTaskCreateResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = default!;

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }

    private sealed class KirhaTaskResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("query")]
        public string? Query { get; init; }

        [JsonPropertyName("instruction")]
        public string? Instruction { get; init; }

        [JsonPropertyName("result")]
        public JsonElement? Result { get; init; }

        [JsonPropertyName("error")]
        public JsonElement? Error { get; init; }

        [JsonPropertyName("createdAt")]
        public string? CreatedAt { get; init; }

        [JsonPropertyName("updatedAt")]
        public string? UpdatedAt { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }
}
