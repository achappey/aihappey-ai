using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.EmpirioLabsAI;

public partial class EmpirioLabsAIProvider
{
    private const string EmpirioAgentModel = "manus";
    private const string EmpirioAgentEndpoint = "v1/agents/run";
    private const string EmpirioAgentToolName = "empiriolabs_agent_task";
    private static readonly TimeSpan EmpirioAgentDefaultPollInterval = TimeSpan.FromSeconds(3);

    private static bool IsEmpirioAgentModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var local = model.Trim().Trim('/');
        if (local.StartsWith("empiriolabsai/", StringComparison.OrdinalIgnoreCase))
            local = local["empiriolabsai/".Length..];
        return string.Equals(local, EmpirioAgentModel, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AIResponse> ExecuteEmpirioAgentUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var payload = BuildEmpirioAgentPayload(request, stream: false);
        string? taskId = null;
        try
        {
            var task = await SendEmpirioAgentJsonAsync(HttpMethod.Post, EmpirioAgentEndpoint, payload, "run agent", cancellationToken);
            taskId = RequireEmpirioTaskId(task);
            var pollInterval = GetEmpirioPollInterval(payload);
            while (!IsEmpirioAgentTerminal(task))
            {
                await Task.Delay(pollInterval, cancellationToken);
                task = await GetEmpirioAgentTaskAsync(taskId, cancellationToken);
            }

            return CreateEmpirioAgentResponse(request, payload, task);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(taskId)) await StopEmpirioAgentTaskSafeAsync(taskId);
            throw;
        }
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamEmpirioAgentUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var payload = BuildEmpirioAgentPayload(request, stream: true);
        var timestamp = DateTimeOffset.UtcNow;
        var toolCallId = $"empiriolabs-agent-{Guid.NewGuid():N}";
        string? taskId = null;
        JsonElement? latest = null;
        var terminal = false;
        var textStarted = false;
        var emittedText = new StringBuilder();

        yield return CreateEmpirioAgentEvent(toolCallId, "tool-input-available", new AIToolInputAvailableEventData
        {
            ToolName = EmpirioAgentToolName,
            Title = "EmpirioLabs agent task",
            Input = JsonSerializer.SerializeToElement(payload, EmpirioMediaJson),
            ProviderExecuted = true,
            ProviderMetadata = CreateEmpirioAgentProviderMetadata(null, "submitted")
        }, timestamp, CreateEmpirioAgentMetadata(request, payload, null));

        try
        {
            using var httpRequest = CreateEmpirioAgentRequest(HttpMethod.Post, EmpirioAgentEndpoint, payload, true);
            using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"EmpirioLabs agent stream failed ({(int)response.StatusCode}): {error}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            string? eventName = null;
            var dataLines = new List<string>();
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is not null && line.Length > 0)
                {
                    if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                        eventName = line["event:".Length..].Trim();
                    else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        dataLines.Add(line["data:".Length..].TrimStart());
                    continue;
                }

                if (dataLines.Count > 0 && TryParseEmpirioAgentSseData(dataLines, out var eventData))
                {
                    latest = eventData;
                    taskId ??= FindEmpirioString(eventData, "task_id", "taskId");
                    var status = FindEmpirioString(eventData, "status") ?? StatusFromEmpirioEventName(eventName);
                    terminal = IsEmpirioAgentTerminalStatus(status);
                    var metadata = CreateEmpirioAgentMetadata(request, payload, eventData);

                    yield return CreateEmpirioAgentEvent(toolCallId, "tool-output-available", new AIToolOutputAvailableEventData
                    {
                        ToolName = EmpirioAgentToolName,
                        Output = CreateEmpirioAgentToolOutput(eventData, taskId, status),
                        ProviderExecuted = true,
                        Dynamic = true,
                        Preliminary = !terminal,
                        ProviderMetadata = CreateEmpirioAgentProviderMetadata(taskId, eventName ?? status ?? "event")
                    }, DateTimeOffset.UtcNow, metadata);

                    var fragment = ExtractEmpirioAgentStreamText(eventData, eventName);
                    if (!string.IsNullOrEmpty(fragment))
                    {
                        if (!textStarted)
                        {
                            textStarted = true;
                            yield return CreateEmpirioAgentEvent(taskId ?? toolCallId, "text-start", new AITextStartEventData
                            {
                                ProviderMetadata = CreateEmpirioAgentLooseMetadata(taskId, eventName ?? "output")
                            }, DateTimeOffset.UtcNow, metadata);
                        }

                        emittedText.Append(fragment);
                        yield return CreateEmpirioAgentEvent(taskId ?? toolCallId, "text-delta", new AITextDeltaEventData
                        {
                            Delta = fragment,
                            ProviderMetadata = CreateEmpirioAgentLooseMetadata(taskId, eventName ?? "output")
                        }, DateTimeOffset.UtcNow, metadata);
                    }
                }

                eventName = null;
                dataLines.Clear();
                if (line is null || terminal) break;
            }

            if (string.IsNullOrWhiteSpace(taskId))
                throw new InvalidOperationException("EmpirioLabs agent stream did not return a task_id.");

            var finalTask = latest is { } value && IsCompleteEmpirioAgentTask(value) && IsEmpirioAgentTerminal(value)
                ? value
                : await GetEmpirioAgentTaskAsync(taskId, cancellationToken);
            while (!IsEmpirioAgentTerminal(finalTask))
            {
                await Task.Delay(GetEmpirioPollInterval(payload), cancellationToken);
                finalTask = await GetEmpirioAgentTaskAsync(taskId, cancellationToken);
            }

            terminal = true;
            var finalStatus = FindEmpirioString(finalTask, "status") ?? "failed";
            var failed = IsEmpirioAgentFailureStatus(finalStatus);
            var finalMetadata = CreateEmpirioAgentMetadata(request, payload, finalTask);
            var finalText = FindEmpirioString(finalTask, "output");
            var suffix = GetUnemittedEmpirioText(emittedText.ToString(), finalText);
            if (!string.IsNullOrEmpty(suffix))
            {
                if (!textStarted)
                {
                    textStarted = true;
                    yield return CreateEmpirioAgentEvent(taskId, "text-start", new AITextStartEventData(), DateTimeOffset.UtcNow, finalMetadata);
                }
                yield return CreateEmpirioAgentEvent(taskId, "text-delta", new AITextDeltaEventData { Delta = suffix }, DateTimeOffset.UtcNow, finalMetadata);
            }
            if (textStarted)
                yield return CreateEmpirioAgentEvent(taskId, "text-end", new AITextEndEventData(), DateTimeOffset.UtcNow, finalMetadata);

            foreach (var artifactEvent in CreateEmpirioArtifactEvents(taskId, finalTask, finalMetadata))
                yield return artifactEvent;

            if (failed)
                yield return CreateEmpirioAgentEvent(taskId, "error", new AIErrorEventData
                {
                    ErrorText = FindEmpirioString(finalTask, "error", "message")
                                ?? $"EmpirioLabs agent task ended with status '{finalStatus}'."
                }, DateTimeOffset.UtcNow, finalMetadata);

            var usage = CloneEmpirioProperty(finalTask, "usage");
            yield return CreateEmpirioAgentEvent(taskId, "finish", new AIFinishEventData
            {
                FinishReason = failed ? "error" : "stop",
                Model = EmpirioAgentModel.ToModelId(GetIdentifier()),
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    EmpirioAgentModel.ToModelId(GetIdentifier()), DateTimeOffset.UtcNow, usage,
                    additionalProperties: new Dictionary<string, object?> { ["task_id"] = taskId })
            }, DateTimeOffset.UtcNow, finalMetadata);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested && !string.IsNullOrWhiteSpace(taskId) && !terminal)
                await StopEmpirioAgentTaskSafeAsync(taskId);
        }
    }

    private JsonObject BuildEmpirioAgentPayload(AIRequest request, bool stream)
    {
        var options = GetEmpirioAgentOptions(request.Metadata);
        var payload = options is null ? new JsonObject() : options;
        foreach (var reserved in new[] { "model", "input", "task_id", "taskId", "stream", "attachments" }) payload.Remove(reserved);

        var input = ExtractLatestEmpirioAgentUserText(request);
        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("EmpirioLabs Manus requires a non-empty latest user message, input, or instructions.");

        payload["model"] = EmpirioAgentModel;
        payload["input"] = input;
        payload["stream"] = stream;
        if (TryFindEmpirioTaskId(request, out var taskId)) payload["task_id"] = taskId;

        var attachments = ExtractEmpirioAgentAttachments(request);
        if (attachments.Count > 0) payload["attachments"] = attachments;
        return payload;
    }

    private JsonObject? GetEmpirioAgentOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue(GetIdentifier(), out var raw) || raw is null) return null;
        try
        {
            var node = raw is JsonElement element ? JsonNode.Parse(element.GetRawText()) : JsonSerializer.SerializeToNode(raw, EmpirioMediaJson);
            return node as JsonObject;
        }
        catch { return null; }
    }

    private static string ExtractLatestEmpirioAgentUserText(AIRequest request)
    {
        var text = request.Input?.Items?
            .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(item => string.Join("\n", (item.Content ?? []).OfType<AITextContentPart>().Select(part => part.Text).Where(value => !string.IsNullOrWhiteSpace(value))))
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return text ?? request.Input?.Text ?? request.Instructions ?? string.Empty;
    }

    private static JsonArray ExtractEmpirioAgentAttachments(AIRequest request)
    {
        var latest = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = latest?.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        if (files.Count > 10) throw new ArgumentException("EmpirioLabs agent requests accept at most 10 attachments.", nameof(request));
        var result = new JsonArray();
        foreach (var file in files)
        {
            var url = file.Data?.ToString();
            if (string.IsNullOrWhiteSpace(url) || !(url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
                throw new ArgumentException("EmpirioLabs agent attachments must be public HTTP(S) URLs or data URIs.", nameof(request));

            if (string.IsNullOrWhiteSpace(file.Filename) && string.IsNullOrWhiteSpace(file.MediaType)) result.Add(url);
            else result.Add(new JsonObject { ["url"] = url, ["filename"] = file.Filename, ["mime_type"] = file.MediaType });
        }
        return result;
    }

    private bool TryFindEmpirioTaskId(AIRequest request, out string taskId)
    {
        taskId = TryExtractEmpirioTaskId(request.Metadata) ?? TryExtractEmpirioTaskId(request.Input?.Metadata) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(taskId)) return true;
        foreach (var item in request.Input?.Items ?? [])
        {
            taskId = TryExtractEmpirioTaskId(item.Metadata) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(taskId)) return true;
            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (tool.ProviderExecuted != true || !string.Equals(tool.ToolName, EmpirioAgentToolName, StringComparison.OrdinalIgnoreCase)) continue;
                taskId = TryExtractEmpirioTaskId(tool.Output) ?? TryExtractEmpirioTaskId(tool.Metadata)
                         ?? TryExtractEmpirioTaskId(tool.Input) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(taskId)) return true;
            }
        }
        taskId = string.Empty;
        return false;
    }

    private static string? TryExtractEmpirioTaskId(object? value)
    {
        if (value is null) return null;
        JsonElement element;
        try { element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, EmpirioMediaJson); }
        catch { return null; }
        return FindEmpirioString(element, "task_id", "taskId", "empiriolabsai.agent.task_id");
    }

    private HttpRequestMessage CreateEmpirioAgentRequest(HttpMethod method, string endpoint, JsonObject? payload, bool stream)
    {
        var message = new HttpRequestMessage(method, endpoint);
        if (payload is not null) message.Content = new StringContent(payload.ToJsonString(EmpirioMediaJson), Encoding.UTF8, MediaTypeNames.Application.Json);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : MediaTypeNames.Application.Json));
        return message;
    }

    private async Task<JsonElement> SendEmpirioAgentJsonAsync(HttpMethod method, string endpoint, JsonObject? payload, string operation, CancellationToken cancellationToken)
    {
        using var request = CreateEmpirioAgentRequest(method, endpoint, payload, false);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"EmpirioLabs agent {operation} failed ({(int)response.StatusCode}): {raw}");
        try { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
        catch (JsonException exception) { throw new InvalidOperationException($"EmpirioLabs agent {operation} returned invalid JSON: {raw}", exception); }
    }

    private Task<JsonElement> GetEmpirioAgentTaskAsync(string taskId, CancellationToken cancellationToken)
        => SendEmpirioAgentJsonAsync(HttpMethod.Get, $"v1/agents/{Uri.EscapeDataString(taskId)}", null, "get task", cancellationToken);

    private async Task StopEmpirioAgentTaskSafeAsync(string taskId)
    {
        try { await SendEmpirioAgentJsonAsync(HttpMethod.Post, $"v1/agents/{Uri.EscapeDataString(taskId)}/stop", new JsonObject(), "stop task", CancellationToken.None); }
        catch { }
    }

    private AIResponse CreateEmpirioAgentResponse(AIRequest request, JsonObject payload, JsonElement task)
    {
        var taskId = RequireEmpirioTaskId(task);
        var status = FindEmpirioString(task, "status") ?? "failed";
        var failed = IsEmpirioAgentFailureStatus(status);
        var metadata = CreateEmpirioAgentMetadata(request, payload, task);
        var items = new List<AIOutputItem>
        {
            new()
            {
                Type = "tool-call", Role = "assistant",
                Content = [new AIToolCallContentPart
                {
                    Type = "tool-call", ToolCallId = $"empiriolabs-agent-{taskId}", ToolName = EmpirioAgentToolName,
                    Title = "EmpirioLabs agent task", Input = JsonSerializer.SerializeToElement(payload, EmpirioMediaJson),
                    Output = CreateEmpirioAgentToolOutput(task, taskId, status), ProviderExecuted = true,
                    State = failed ? "output-error" : "output-available", Metadata = metadata
                }], Metadata = metadata
            }
        };
        var output = FindEmpirioString(task, "output");
        if (!string.IsNullOrWhiteSpace(output)) items.Add(new AIOutputItem
        {
            Type = "message", Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = output, Metadata = metadata }], Metadata = metadata
        });
        items.AddRange(CreateEmpirioArtifactOutputItems(task));
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = EmpirioAgentModel.ToModelId(GetIdentifier()),
            Status = failed ? "failed" : status == "completed" ? "completed" : "in_progress",
            Usage = CloneEmpirioProperty(task, "usage"), Metadata = metadata,
            Output = new AIOutput { Items = items, Metadata = metadata }
        };
    }

    private static IEnumerable<AIOutputItem> CreateEmpirioArtifactOutputItems(JsonElement task)
    {
        if (!task.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array) yield break;
        foreach (var artifact in artifacts.EnumerateArray())
        {
            var url = FindEmpirioString(artifact, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            var type = FindEmpirioString(artifact, "type") ?? "artifact";
            yield return new AIOutputItem
            {
                Type = "file", Content = [new AIFileContentPart { Type = "file", Filename = type, MediaType = "application/octet-stream", Data = url, Metadata = new() { ["empiriolabs.artifact"] = artifact.Clone() } }],
                Metadata = new() { ["url"] = url, ["type"] = type, ["empiriolabs.artifact"] = artifact.Clone() }
            };
        }
    }

    private IEnumerable<AIStreamEvent> CreateEmpirioArtifactEvents(string taskId, JsonElement task, Dictionary<string, object?> metadata)
    {
        if (!task.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array) yield break;
        foreach (var artifact in artifacts.EnumerateArray())
        {
            var url = FindEmpirioString(artifact, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            yield return CreateEmpirioAgentEvent(taskId, "file", new AIFileEventData
            {
                Url = url, Filename = FindEmpirioString(artifact, "type"), MediaType = "application/octet-stream",
                ProviderMetadata = new() { [GetIdentifier()] = new() { ["artifact"] = artifact.Clone(), ["task_id"] = taskId } }
            }, DateTimeOffset.UtcNow, metadata);
        }
    }

    private Dictionary<string, object?> CreateEmpirioAgentMetadata(AIRequest request, JsonObject payload, JsonElement? task)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["empiriolabsai.agent"] = true, ["empiriolabsai.model"] = EmpirioAgentModel,
            ["empiriolabsai.request.payload"] = JsonSerializer.SerializeToElement(payload, EmpirioMediaJson),
            ["empiriolabsai.requested_model"] = request.Model
        };
        if (task is { ValueKind: JsonValueKind.Object } value)
        {
            metadata["empiriolabsai.agent.task"] = value.Clone();
            metadata["empiriolabsai.agent.task_id"] = FindEmpirioString(value, "task_id", "taskId");
            metadata["empiriolabsai.agent.status"] = FindEmpirioString(value, "status");
            metadata["task_id"] = FindEmpirioString(value, "task_id", "taskId");
        }
        return metadata;
    }

    private Dictionary<string, Dictionary<string, object>> CreateEmpirioAgentProviderMetadata(string? taskId, string stage)
        => new() { [GetIdentifier()] = new() { ["type"] = EmpirioAgentToolName, ["task_id"] = taskId ?? string.Empty, ["taskId"] = taskId ?? string.Empty, ["stage"] = stage } };

    private Dictionary<string, object>? CreateEmpirioAgentLooseMetadata(string? taskId, string stage)
        => new() { ["task_id"] = taskId ?? string.Empty, ["taskId"] = taskId ?? string.Empty, ["stage"] = stage };

    private AIStreamEvent CreateEmpirioAgentEvent(string id, string type, object data, DateTimeOffset timestamp, Dictionary<string, object?> metadata)
        => new() { ProviderId = GetIdentifier(), Event = new AIEventEnvelope { Id = id, Type = type, Timestamp = timestamp, Data = data }, Metadata = metadata };

    private static object CreateEmpirioAgentToolOutput(JsonElement task, string? taskId, string? status)
        => new { task_id = taskId, taskId, status, task = task.Clone() };

    private static bool TryParseEmpirioAgentSseData(List<string> lines, out JsonElement data)
    {
        data = default;
        var raw = string.Join("\n", lines).Trim();
        if (raw.Length == 0 || raw == "[DONE]") return false;
        try { using var document = JsonDocument.Parse(raw); data = document.RootElement.Clone(); return true; }
        catch (JsonException) { return false; }
    }

    private static string? ExtractEmpirioAgentStreamText(JsonElement data, string? eventName)
    {
        if (eventName?.Contains("status", StringComparison.OrdinalIgnoreCase) == true) return null;
        foreach (var name in new[] { "delta", "text_delta", "content_delta", "message", "text" })
        {
            var value = FindEmpirioString(data, name);
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return eventName?.Contains("output", StringComparison.OrdinalIgnoreCase) == true ? FindEmpirioString(data, "output") : null;
    }

    private static string GetUnemittedEmpirioText(string emitted, string? finalText)
    {
        if (string.IsNullOrEmpty(finalText) || string.Equals(emitted, finalText, StringComparison.Ordinal)) return string.Empty;
        return finalText.StartsWith(emitted, StringComparison.Ordinal) ? finalText[emitted.Length..] : finalText;
    }

    private static string RequireEmpirioTaskId(JsonElement task)
        => FindEmpirioString(task, "task_id", "taskId") is { Length: > 0 } id ? id : throw new InvalidOperationException("EmpirioLabs agent response did not include task_id.");

    private static bool IsCompleteEmpirioAgentTask(JsonElement task)
        => task.ValueKind == JsonValueKind.Object && (task.TryGetProperty("output", out _) || task.TryGetProperty("usage", out _) || task.TryGetProperty("artifacts", out _));

    private static bool IsEmpirioAgentTerminal(JsonElement task) => IsEmpirioAgentTerminalStatus(FindEmpirioString(task, "status"));
    private static bool IsEmpirioAgentTerminalStatus(string? status) => status?.ToLowerInvariant() is "completed" or "failed" or "stopped";
    private static bool IsEmpirioAgentFailureStatus(string? status) => status?.ToLowerInvariant() is "failed" or "stopped";

    private static string? StatusFromEmpirioEventName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var status in new[] { "queued", "running", "completed", "failed", "stopped" })
            if (name.Contains(status, StringComparison.OrdinalIgnoreCase)) return status;
        return null;
    }

    private static TimeSpan GetEmpirioPollInterval(JsonObject payload)
    {
        try
        {
            var seconds = payload["poll_interval_seconds"]?.GetValue<double?>();
            return seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : EmpirioAgentDefaultPollInterval;
        }
        catch { return EmpirioAgentDefaultPollInterval; }
    }

    private static object? CloneEmpirioProperty(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.Clone() : null;

    private static string? FindEmpirioString(JsonElement value, params string[] names)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    return property.Value.ToString();
            }
            foreach (var property in value.EnumerateObject())
            {
                var nested = FindEmpirioString(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var nested = FindEmpirioString(item, names);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }
}
