using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Yutori;

public partial class YutoriProvider
{
    private static readonly JsonSerializerOptions YutoriJson = JsonSerializerOptions.Web;
    private static readonly TimeSpan YutoriPollInterval = TimeSpan.FromSeconds(1);

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var target = ResolveYutoriTarget(request);
        var prompt = BuildYutoriPrompt(request);
        var payload = BuildYutoriPayload(request, target, prompt);
        var submittedPayload = JsonSerializer.SerializeToElement(payload, YutoriJson);
        var created = await CreateYutoriTaskAsync(target, payload, cancellationToken);
        var completed = await WaitForYutoriTaskAsync(target, created.TaskId, cancellationToken);
        var trajectory = target.Kind == YutoriTaskKind.Browser && IsYutoriSuccessful(completed.Status)
            ? await GetYutoriTrajectorySafeAsync(completed.TaskId, cancellationToken)
            : null;

        return CreateYutoriResponse(request, target, submittedPayload, created, completed, trajectory);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var target = ResolveYutoriTarget(request);
        var prompt = BuildYutoriPrompt(request);
        var payload = BuildYutoriPayload(request, target, prompt);
        var submittedPayload = JsonSerializer.SerializeToElement(payload, YutoriJson);
        var providerId = GetIdentifier();
        var eventId = request.Id ?? $"yutori_{Guid.NewGuid():N}";
        var pendingToolCallId = $"yutori_{target.Route}_{eventId}";
        var timestamp = DateTimeOffset.UtcNow;

        yield return CreateYutoriEvent(providerId, pendingToolCallId, "tool-input-start", new AIToolInputStartEventData
        {
            ToolName = target.ToolName,
            Title = target.ToolTitle,
            ProviderExecuted = true,
            ProviderMetadata = CreateYutoriToolProviderMetadata(providerId, target, pendingToolCallId, "tool_use")
        }, timestamp, null);

        yield return CreateYutoriEvent(providerId, pendingToolCallId, "tool-input-available", new AIToolInputAvailableEventData
        {
            ToolName = target.ToolName,
            Title = target.ToolTitle,
            Input = submittedPayload,
            ProviderExecuted = true,
            ProviderMetadata = CreateYutoriToolProviderMetadata(providerId, target, pendingToolCallId, "tool_use")
        }, timestamp, null);

        var created = await CreateYutoriTaskAsync(target, payload, cancellationToken);
        var toolCallId = $"yutori_{target.Route}_{created.TaskId}";
        var queuedMetadata = CreateYutoriMetadata(request, target, submittedPayload, created, created, null);

        yield return CreateYutoriEvent(providerId, pendingToolCallId, "tool-output-available", new AIToolOutputAvailableEventData
        {
            ToolName = target.ToolName,
            Output = created.Raw,
            ProviderExecuted = true,
            Dynamic = true,
            Preliminary = true,
            ProviderMetadata = CreateYutoriToolProviderMetadata(providerId, target, pendingToolCallId, "tool_result")
        }, DateTimeOffset.UtcNow, queuedMetadata);

        var completed = await WaitForYutoriTaskAsync(target, created.TaskId, cancellationToken);
        var trajectory = target.Kind == YutoriTaskKind.Browser && IsYutoriSuccessful(completed.Status)
            ? await GetYutoriTrajectorySafeAsync(completed.TaskId, cancellationToken)
            : null;
        var metadata = CreateYutoriMetadata(request, target, submittedPayload, created, completed, trajectory);
        var failed = !IsYutoriSuccessful(completed.Status);

        if (failed)
        {
            yield return CreateYutoriEvent(providerId, pendingToolCallId, "tool-output-error", new AIToolOutputErrorEventData
            {
                ToolCallId = pendingToolCallId,
                ErrorText = GetYutoriFailureMessage(completed),
                ProviderExecuted = true,
                Dynamic = true,
                ProviderMetadata = CreateYutoriToolProviderMetadata(providerId, target, pendingToolCallId, "tool_result")
            }, DateTimeOffset.UtcNow, metadata);

            yield return CreateYutoriEvent(providerId, eventId, "error", new AIErrorEventData
            {
                ErrorText = GetYutoriFailureMessage(completed)
            }, DateTimeOffset.UtcNow, metadata);
        }
        else
        {
            yield return CreateYutoriEvent(providerId, pendingToolCallId, "tool-output-available", new AIToolOutputAvailableEventData
            {
                ToolName = target.ToolName,
                Output = completed.Raw,
                ProviderExecuted = true,
                Dynamic = true,
                Preliminary = false,
                ProviderMetadata = CreateYutoriToolProviderMetadata(providerId, target, pendingToolCallId, "tool_result")
            }, DateTimeOffset.UtcNow, metadata);
        }

        if (!string.IsNullOrWhiteSpace(completed.Result))
        {
            yield return CreateYutoriEvent(providerId, eventId, "text-start", new AITextStartEventData(), DateTimeOffset.UtcNow, metadata);
            yield return CreateYutoriEvent(providerId, eventId, "text-delta", new AITextDeltaEventData { Delta = completed.Result! }, DateTimeOffset.UtcNow, metadata);
            yield return CreateYutoriEvent(providerId, eventId, "text-end", new AITextEndEventData(), DateTimeOffset.UtcNow, metadata);
        }

        if (completed.StructuredResult is JsonElement structuredResult)
        {
            yield return CreateYutoriEvent(providerId, eventId, "data-yutori.structured-result", new AIDataEventData
            {
                Id = completed.TaskId,
                Data = structuredResult.Clone()
            }, DateTimeOffset.UtcNow, metadata);
        }

        foreach (var source in EnumerateYutoriSources(completed))
            yield return CreateYutoriSourceEvent(providerId, eventId, completed.TaskId, source, metadata);

        foreach (var step in trajectory?.Steps ?? [])
        {
            yield return CreateYutoriEvent(providerId, $"{eventId}_trajectory_{step.Step}", "file", new AIFileEventData
            {
                MediaType = "image/webp",
                Filename = CreateYutoriTrajectoryFilename(completed.TaskId, step.Step),
                Url = ToYutoriWebpDataUrl(step.Image),
                ProviderMetadata = CreateYutoriTrajectoryProviderMetadata(providerId, completed.TaskId, step)
            }, DateTimeOffset.UtcNow, metadata);
        }

        var response = CreateYutoriResponse(request, target, submittedPayload, created, completed, trajectory);
        yield return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = eventId,
                Timestamp = DateTimeOffset.UtcNow,
                Output = response.Output,
                Data = new AIFinishEventData
                {
                    FinishReason = failed ? "error" : "stop",
                    Model = request.Model,
                    CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    InputTokens = 0,
                    OutputTokens = 0,
                    TotalTokens = 0,
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        request.Model ?? $"yutori/{target.Route}",
                        DateTimeOffset.UtcNow,
                        usage: response.Usage,
                        inputTokens: 0,
                        outputTokens: 0,
                        totalTokens: 0)
                }
            },
            Metadata = metadata
        };
    }

    private static YutoriTarget ResolveYutoriTarget(AIRequest request)
    {
        var model = request.Model?.Trim() ?? string.Empty;
        const string prefix = "yutori/";
        if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            model = model[prefix.Length..];

        return model.ToLowerInvariant() switch
        {
            "research" => new YutoriTarget(YutoriTaskKind.Research, "research", "v1/research/tasks", "yutori_research", "Yutori research task"),
            "browser" or "browsing" => new YutoriTarget(YutoriTaskKind.Browser, "browser", "v1/browsing/tasks", "yutori_browser", "Yutori browser task"),
            _ => throw new InvalidOperationException($"Unsupported Yutori model '{request.Model}'. Use 'yutori/research' or 'yutori/browser'.")
        };
    }

    private static string BuildYutoriPrompt(AIRequest request)
    {
        var lastUserText = request.Input?.Items?
            .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(item => string.Join("\n", (item.Content ?? [])
                .OfType<AITextContentPart>()
                .Select(part => part.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))))
            .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));

        var prompt = lastUserText ?? request.Input?.Text ?? request.Instructions;
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Yutori requires non-empty text from the latest user message, input, or instructions.");

        return prompt;
    }

    private Dictionary<string, JsonElement> BuildYutoriPayload(AIRequest request, YutoriTarget target, string prompt)
    {
        var payload = GetRawYutoriProviderOptions(request);
        payload[target.Kind == YutoriTaskKind.Research ? "query" : "task"] = JsonSerializer.SerializeToElement(prompt, YutoriJson);

        if (target.Kind == YutoriTaskKind.Browser
            && (!payload.TryGetValue("start_url", out var startUrl)
                || startUrl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(startUrl.GetString())))
        {
            throw new InvalidOperationException("Yutori browser requires backend-native provider option 'start_url'.");
        }

        if (!payload.ContainsKey("output_schema") && TryExtractYutoriOutputSchema(request.ResponseFormat) is JsonElement schema)
            payload["output_schema"] = schema;

        return payload;
    }

    private Dictionary<string, JsonElement> GetRawYutoriProviderOptions(AIRequest request)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var options = request.Metadata.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (options.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in options.EnumerateObject())
            result[property.Name] = property.Value.Clone();

        return result;
    }

    private static JsonElement? TryExtractYutoriOutputSchema(object? format)
    {
        if (format is null)
            return null;

        var schema = format.GetJSONSchema();
        if (schema?.JsonSchema is not null)
        {
            var element = schema.JsonSchema.Schema;
            if (element.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return element.Clone();
        }

        try
        {
            var raw = JsonSerializer.SerializeToElement(format, YutoriJson);
            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("json_schema", out var jsonSchema)
                && jsonSchema.ValueKind == JsonValueKind.Object
                && jsonSchema.TryGetProperty("schema", out var nestedSchema)
                && nestedSchema.ValueKind == JsonValueKind.Object)
                return nestedSchema.Clone();

            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("schema", out var directSchema)
                && directSchema.ValueKind == JsonValueKind.Object)
                return directSchema.Clone();
        }
        catch
        {
            // The response format is optional; unrecognized formats are ignored.
        }

        return null;
    }

    private async Task<YutoriTask> CreateYutoriTaskAsync(
        YutoriTarget target,
        Dictionary<string, JsonElement> payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, target.Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, YutoriJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        return await SendYutoriTaskRequestAsync(request, $"create {target.Route} task", cancellationToken);
    }

    private async Task<YutoriTask> WaitForYutoriTaskAsync(
        YutoriTarget target,
        string taskId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{target.Endpoint}/{Uri.EscapeDataString(taskId)}");
            var task = await SendYutoriTaskRequestAsync(request, $"poll {target.Route} task", cancellationToken);
            if (IsYutoriTerminal(task.Status))
                return task;

            await Task.Delay(YutoriPollInterval, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task<YutoriTask> SendYutoriTaskRequestAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Yutori {operation} failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return ParseYutoriTask(document.RootElement);
    }

    private async Task<YutoriTrajectory?> GetYutoriTrajectorySafeAsync(string taskId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"v1/browsing/tasks/{Uri.EscapeDataString(taskId)}/trajectory?output_type=json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var steps = new List<YutoriTrajectoryStep>();
            if (root.TryGetProperty("steps", out var stepsElement) && stepsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in stepsElement.EnumerateArray())
                {
                    var image = GetYutoriString(step, "image");
                    if (string.IsNullOrWhiteSpace(image))
                        continue;

                    steps.Add(new YutoriTrajectoryStep(GetYutoriInt(step, "step") ?? steps.Count + 1, image!, step.Clone()));
                }
            }

            return new YutoriTrajectory(GetYutoriString(root, "task_id") ?? taskId, steps, root.Clone());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private AIResponse CreateYutoriResponse(
        AIRequest request,
        YutoriTarget target,
        JsonElement submittedPayload,
        YutoriTask created,
        YutoriTask completed,
        YutoriTrajectory? trajectory)
    {
        var failed = !IsYutoriSuccessful(completed.Status);
        var toolCallId = $"yutori_{target.Route}_{completed.TaskId}";
        var output = new List<AIOutputItem>
        {
            new()
            {
                Type = "tool-call",
                Role = "assistant",
                Content =
                [
                    new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = toolCallId,
                        ToolName = target.ToolName,
                        Title = target.ToolTitle,
                        Input = submittedPayload,
                        Output = completed.Raw,
                        State = failed ? "output-error" : "output-available",
                        ProviderExecuted = true,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["yutori.task_id"] = completed.TaskId,
                            ["yutori.status"] = completed.Status,
                            ["yutori.view_url"] = completed.ViewUrl,
                            ["yutori.raw"] = completed.Raw
                        }
                    }
                ]
            }
        };

        if (!string.IsNullOrWhiteSpace(completed.Result))
        {
            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = completed.Result!,
                        Metadata = completed.StructuredResult is JsonElement structured
                            ? new Dictionary<string, object?> { ["yutori.structured_result"] = structured.Clone() }
                            : null
                    }
                ]
            });
        }
        else if (completed.StructuredResult is JsonElement structured)
        {
            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart { Type = "text", Text = structured.GetRawText(), Metadata = new Dictionary<string, object?> { ["yutori.structured_result"] = structured.Clone() } }]
            });
        }

        output.AddRange(EnumerateYutoriSources(completed).Select(CreateYutoriSourceOutputItem));

        foreach (var step in trajectory?.Steps ?? [])
        {
            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AIFileContentPart
                    {
                        Type = "file",
                        MediaType = "image/webp",
                        Filename = CreateYutoriTrajectoryFilename(completed.TaskId, step.Step),
                        Data = ToYutoriWebpDataUrl(step.Image),
                        Metadata = new Dictionary<string, object?>
                        {
                            ["yutori.task_id"] = completed.TaskId,
                            ["yutori.trajectory.step"] = step.Step,
                            ["yutori.trajectory.raw"] = step.Raw
                        }
                    }
                ]
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model ?? $"yutori/{target.Route}",
            Status = failed ? "failed" : "completed",
            Usage = CreateYutoriUsage(),
            Output = new AIOutput { Items = output },
            Metadata = CreateYutoriMetadata(request, target, submittedPayload, created, completed, trajectory)
        };
    }

    private static YutoriTask ParseYutoriTask(JsonElement root)
    {
        JsonElement? structuredResult = root.TryGetProperty("structured_result", out var structured)
                                        && structured.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? structured.Clone()
            : null;
        var updates = new List<YutoriUpdate>();
        if (root.TryGetProperty("updates", out var updatesElement) && updatesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var update in updatesElement.EnumerateArray())
            {
                var citations = new List<YutoriCitation>();
                if (update.TryGetProperty("citations", out var citationsElement) && citationsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var citation in citationsElement.EnumerateArray())
                    {
                        var url = GetYutoriString(citation, "url");
                        if (!string.IsNullOrWhiteSpace(url))
                            citations.Add(new YutoriCitation(GetYutoriString(citation, "id"), url!, citation.Clone()));
                    }
                }
                updates.Add(new YutoriUpdate(GetYutoriString(update, "id"), GetYutoriString(update, "content"), citations, update.Clone()));
            }
        }

        return new YutoriTask(
            GetYutoriString(root, "task_id") ?? throw new InvalidOperationException("Yutori task response did not include task_id."),
            GetYutoriString(root, "view_url"),
            GetYutoriString(root, "status") ?? "queued",
            GetYutoriString(root, "result"),
            structuredResult,
            GetYutoriString(root, "structured_output_status"),
            GetYutoriString(root, "created_at"),
            GetYutoriString(root, "rejection_reason"),
            updates,
            root.Clone());
    }

    private static IEnumerable<YutoriSource> EnumerateYutoriSources(YutoriTask task)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(task.ViewUrl) && seen.Add(task.ViewUrl))
            yield return new YutoriSource(task.ViewUrl!, "Yutori task", "view", null);

        foreach (var citation in task.Updates.SelectMany(update => update.Citations))
        {
            if (seen.Add(citation.Url))
                yield return new YutoriSource(citation.Url, citation.Id ?? citation.Url, "citation", citation.Raw);
        }
    }

    private static AIOutputItem CreateYutoriSourceOutputItem(YutoriSource source)
        => new()
        {
            Type = "source-url",
            Content = [new AITextContentPart { Type = "text", Text = source.Title }],
            Metadata = new Dictionary<string, object?>
            {
                ["source.url"] = source.Url,
                ["source.title"] = source.Title,
                ["chatcompletions.source.url"] = source.Url,
                ["chatcompletions.source.title"] = source.Title,
                ["messages.source.url"] = source.Url,
                ["messages.source.title"] = source.Title,
                ["yutori.source.type"] = source.Type,
                ["yutori.source.raw"] = source.Raw
            }
        };

    private static AIStreamEvent CreateYutoriSourceEvent(
        string providerId,
        string eventId,
        string taskId,
        YutoriSource source,
        Dictionary<string, object?> metadata)
        => CreateYutoriEvent(providerId, $"{eventId}_source_{source.Url}", "source-url", new AISourceUrlEventData
        {
            SourceId = source.Url,
            Url = source.Url,
            Title = source.Title,
            Type = source.Type,
            ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = new Dictionary<string, object>
                {
                    ["task_id"] = taskId,
                    ["source_type"] = source.Type,
                    ["raw"] = source.Raw.HasValue ? source.Raw.Value : new { }
                }
            }
        }, DateTimeOffset.UtcNow, metadata);

    private static Dictionary<string, object?> CreateYutoriMetadata(
        AIRequest request,
        YutoriTarget target,
        JsonElement submittedPayload,
        YutoriTask created,
        YutoriTask completed,
        YutoriTrajectory? trajectory)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["yutori.task_id"] = completed.TaskId,
            ["yutori.task_type"] = target.Route,
            ["yutori.status"] = completed.Status,
            ["yutori.view_url"] = completed.ViewUrl,
            ["yutori.structured_result"] = completed.StructuredResult,
            ["yutori.structured_output_status"] = completed.StructuredOutputStatus,
            ["yutori.created_at"] = completed.CreatedAt,
            ["yutori.rejection_reason"] = completed.RejectionReason,
            ["yutori.request.raw"] = submittedPayload,
            ["yutori.create.raw"] = created.Raw,
            ["yutori.response.raw"] = completed.Raw,
            ["yutori.trajectory.raw"] = trajectory?.Raw,
            ["responses.id"] = completed.TaskId,
            ["responses.object"] = "response",
            ["responses.created_at"] = ParseYutoriCreatedAt(completed.CreatedAt),
            ["responses.completed_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["responses.temperature"] = request.Temperature,
            ["responses.max_output_tokens"] = request.MaxOutputTokens,
            ["chatcompletions.response.id"] = completed.TaskId,
            ["chatcompletions.response.object"] = "chat.completion",
            ["chatcompletions.response.created"] = ParseYutoriCreatedAt(completed.CreatedAt),
            ["chatcompletions.response.model"] = request.Model ?? $"yutori/{target.Route}"
        };

    private static Dictionary<string, Dictionary<string, object>> CreateYutoriToolProviderMetadata(
        string providerId,
        YutoriTarget target,
        string toolCallId,
        string type)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = type,
                ["task_type"] = target.Route,
                ["tool_name"] = target.ToolName,
                ["tool_use_id"] = toolCallId
            }
        };

    private static Dictionary<string, Dictionary<string, object>> CreateYutoriTrajectoryProviderMetadata(
        string providerId,
        string taskId,
        YutoriTrajectoryStep step)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = "file",
                ["file_kind"] = "trajectory_screenshot",
                ["task_id"] = taskId,
                ["step"] = step.Step,
                ["raw"] = step.Raw
            }
        };

    private static AIStreamEvent CreateYutoriEvent(
        string providerId,
        string? id,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data },
            Metadata = metadata
        };

    private static object CreateYutoriUsage()
        => new Dictionary<string, object?>
        {
            ["prompt_tokens"] = 0,
            ["completion_tokens"] = 0,
            ["total_tokens"] = 0
        };

    private static bool IsYutoriTerminal(string? status)
        => string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsYutoriSuccessful(string? status)
        => string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase);

    private static string GetYutoriFailureMessage(YutoriTask task)
        => !string.IsNullOrWhiteSpace(task.RejectionReason)
            ? $"Yutori task '{task.TaskId}' failed: {task.RejectionReason}."
            : $"Yutori task '{task.TaskId}' finished with status '{task.Status}'.";

    private static string ToYutoriWebpDataUrl(string image)
        => image.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? image : $"data:image/webp;base64,{image}";

    private static string CreateYutoriTrajectoryFilename(string taskId, int step)
        => $"yutori-{taskId}-step-{step:D3}.webp";

    private static long ParseYutoriCreatedAt(string? value)
        => DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp.ToUniversalTime().ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string? GetYutoriString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetYutoriInt(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var property)
           && property.TryGetInt32(out var value)
            ? value
            : null;

    private enum YutoriTaskKind
    {
        Research,
        Browser
    }

    private sealed record YutoriTarget(
        YutoriTaskKind Kind,
        string Route,
        string Endpoint,
        string ToolName,
        string ToolTitle);

    private sealed record YutoriTask(
        string TaskId,
        string? ViewUrl,
        string Status,
        string? Result,
        JsonElement? StructuredResult,
        string? StructuredOutputStatus,
        string? CreatedAt,
        string? RejectionReason,
        List<YutoriUpdate> Updates,
        JsonElement Raw);

    private sealed record YutoriUpdate(string? Id, string? Content, List<YutoriCitation> Citations, JsonElement Raw);
    private sealed record YutoriCitation(string? Id, string Url, JsonElement Raw);
    private sealed record YutoriSource(string Url, string Title, string Type, JsonElement? Raw);
    private sealed record YutoriTrajectory(string TaskId, List<YutoriTrajectoryStep> Steps, JsonElement Raw);
    private sealed record YutoriTrajectoryStep(int Step, string Image, JsonElement Raw);
}
