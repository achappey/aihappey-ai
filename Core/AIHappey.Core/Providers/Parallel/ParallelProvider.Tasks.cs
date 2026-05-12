using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Parallel;

public partial class ParallelProvider
{
    private async Task<AIResponse> ExecuteParallelTaskUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var timestamp = DateTimeOffset.UtcNow;
        var run = await CreateParallelTaskRunAsync(request, enableEvents: false, cancellationToken);
        var runId = TryGetString(run, "run_id")
                    ?? throw new InvalidOperationException("Parallel task run create response did not include run_id.");

        var result = await RetrieveParallelTaskResultAsync(runId, cancellationToken);
        var output = TryGetProperty(result, "output", out var outputEl) ? outputEl.Clone() : default;
        var completedRun = TryGetProperty(result, "run", out var runEl) ? runEl.Clone() : run;
        var interactionId = TryGetString(completedRun, "interaction_id") ?? TryGetString(run, "interaction_id");
        var model = request.Model ?? NormalizeParallelModel(request.Model).ToModelId(GetIdentifier());

        var outputItems = new List<AIOutputItem>();
        if (!string.IsNullOrWhiteSpace(interactionId))
            outputItems.Add(CreateParallelInteractionOutputItem(interactionId, completedRun, request));

        outputItems.AddRange(CreateParallelTaskOutputItems(output, completedRun));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = ToUnifiedTaskStatus(TryGetString(completedRun, "status")),
            Output = outputItems.Count == 0 ? null : new AIOutput { Items = outputItems },
            Metadata = CreateParallelTaskResponseMetadata(runId, interactionId, completedRun, output, result, timestamp)
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamParallelTaskUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var timestamp = DateTimeOffset.UtcNow;
        var run = await CreateParallelTaskRunAsync(request, enableEvents: true, cancellationToken);
        var runId = TryGetString(run, "run_id")
                    ?? throw new InvalidOperationException("Parallel task run create response did not include run_id.");
        var interactionId = TryGetString(run, "interaction_id");

        if (!string.IsNullOrWhiteSpace(interactionId))
        {
            foreach (var evt in CreateParallelInteractionToolEvents(interactionId, run, request, timestamp))
                yield return evt;
        }

        var textId = $"parallel-task-{runId}";
        var textStarted = false;
        JsonElement? terminalRun = null;
        JsonElement? terminalOutput = null;
        JsonElement? errorEvent = null;

        await foreach (var rawEvent in StreamParallelTaskRunEventsRawAsync(runId, cancellationToken))
        {
            using var doc = JsonDocument.Parse(rawEvent);
            var evt = doc.RootElement.Clone();
            var eventType = TryGetString(evt, "type") ?? string.Empty;
            var eventTimestamp = TryGetDateTimeOffset(evt, "timestamp") ?? DateTimeOffset.UtcNow;

            switch (eventType)
            {
                case "task_run.progress_msg.plan":
                case "task_run.progress_msg.search":
                case "task_run.progress_msg.result":
                case "task_run.progress_msg.tool_call":
                case "task_run.progress_msg.exec_status":
                    var message = TryGetString(evt, "message");
                    if (string.IsNullOrWhiteSpace(message))
                        break;

                    if (!textStarted)
                    {
                        textStarted = true;
                        yield return CreateParallelStreamEvent(
                            GetIdentifier(),
                            "text-start",
                            textId,
                            new AITextStartEventData { 
//                                ProviderMetadata = CreateLooseProviderMetadata(evt) 
                                },
                            eventTimestamp);
                    }

                    yield return CreateParallelStreamEvent(
                        GetIdentifier(),
                        "text-delta",
                        textId,
                        new AITextDeltaEventData
                        {
                            Delta = message + Environment.NewLine,
                        //    ProviderMetadata = CreateLooseProviderMetadata(evt)
                        },
                        eventTimestamp);
                    break;

                case "task_run.progress_stats":
                    yield return CreateParallelStreamEvent(
                        GetIdentifier(),
                        "data-parallel-task-progress",
                        runId,
                        new AIDataEventData
                        {
                            Id = runId,
                            Data = evt,
                            Transient = true
                        },
                        eventTimestamp);
                    break;

                case "task_run.state":
                    if (TryGetProperty(evt, "run", out var stateRun))
                        terminalRun = stateRun.Clone();
                    if (TryGetProperty(evt, "output", out var stateOutput))
                        terminalOutput = stateOutput.Clone();
                    break;

                case "error":
                    errorEvent = evt.Clone();
                    yield return CreateParallelStreamEvent(
                        GetIdentifier(),
                        "error",
                        runId,
                        new AIErrorEventData { ErrorText = ExtractParallelErrorText(evt) ?? "Parallel task stream error." },
                        eventTimestamp,
                        new Dictionary<string, object?> { ["parallel.raw"] = evt });
                    break;
            }
        }

        if (textStarted)
        {
            yield return CreateParallelStreamEvent(
                GetIdentifier(),
                "text-end",
                textId,
                new AITextEndEventData(),
                DateTimeOffset.UtcNow);
        }

        if (!terminalOutput.HasValue)
        {
            var result = await RetrieveParallelTaskResultAsync(runId, cancellationToken);
            if (TryGetProperty(result, "run", out var resultRun))
                terminalRun = resultRun.Clone();
            if (TryGetProperty(result, "output", out var resultOutput))
                terminalOutput = resultOutput.Clone();
        }

        var finalRun = terminalRun ?? run;
        var finalOutput = terminalOutput;
        var finalInteractionId = TryGetString(finalRun, "interaction_id") ?? interactionId;
        var finalText = finalOutput.HasValue ? ExtractTaskOutputText(finalOutput.Value) : null;

        if (!string.IsNullOrWhiteSpace(finalText))
        {
            var finalTextId = $"parallel-task-output-{runId}";
            yield return CreateParallelStreamEvent(GetIdentifier(), "text-start", finalTextId, new AITextStartEventData(), DateTimeOffset.UtcNow);
            yield return CreateParallelStreamEvent(GetIdentifier(), "text-delta", finalTextId, new AITextDeltaEventData { Delta = finalText }, DateTimeOffset.UtcNow);
            yield return CreateParallelStreamEvent(GetIdentifier(), "text-end", finalTextId, new AITextEndEventData(), DateTimeOffset.UtcNow);
        }

        if (finalOutput.HasValue)
        {
            foreach (var sourceEvent in CreateParallelSourceEvents(finalOutput.Value, DateTimeOffset.UtcNow))
                yield return sourceEvent;
        }

        yield return CreateParallelStreamEvent(
            GetIdentifier(),
            "finish",
            runId,
            new AIFinishEventData
            {
                FinishReason = errorEvent.HasValue ? "error" : ResolveParallelFinishReason(TryGetString(finalRun, "status")),
                Model = request.Model ?? NormalizeParallelModel(request.Model).ToModelId(GetIdentifier()),
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    request.Model ?? NormalizeParallelModel(request.Model).ToModelId(GetIdentifier()),
                    DateTimeOffset.UtcNow,
                    additionalProperties: new Dictionary<string, object?>
                    {
                        [GetIdentifier()] = new Dictionary<string, object?>
                        {
                            ["run_id"] = runId,
                            ["interaction_id"] = finalInteractionId,
                            ["run"] = finalRun,
                            ["output"] = finalOutput
                        }
                    })
            },
            DateTimeOffset.UtcNow,
            CreateParallelTaskResponseMetadata(runId, finalInteractionId, finalRun, finalOutput, null, timestamp));
    }

    private async Task<JsonElement> CreateParallelTaskRunAsync(AIRequest request, bool enableEvents, CancellationToken cancellationToken)
    {
        var payload = BuildParallelTaskRunPayload(request, enableEvents);
        return await SendParallelJsonAsync(HttpMethod.Post, TaskRunsPath, payload, "Parallel task run create", cancellationToken);
    }

    private async Task<JsonElement> RetrieveParallelTaskResultAsync(string runId, CancellationToken cancellationToken)
        => await SendParallelJsonAsync(
            HttpMethod.Get,
            $"{TaskRunsPath}/{Uri.EscapeDataString(runId)}/result",
            operation: "Parallel task run result",
            cancellationToken: cancellationToken);

    private Dictionary<string, object?> BuildParallelTaskRunPayload(AIRequest request, bool enableEvents)
    {
        var processor = NormalizeParallelModel(request.Model);
        if (string.IsNullOrWhiteSpace(processor))
            throw new ArgumentException("Parallel Task API requires a processor model.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["processor"] = processor,
            ["input"] = BuildTaskInput(request),
            ["enable_events"] = enableEvents
        };

        if (TryFindParallelInteractionId(request, out var previousInteractionId))
            payload["previous_interaction_id"] = previousInteractionId;

        var taskSpec = BuildTaskSpec(request);
        if (taskSpec is not null)
            payload["task_spec"] = taskSpec;

        var sourcePolicy = request.Metadata?.GetProviderOption<object>(GetIdentifier(), "source_policy");
        if (sourcePolicy is not null)
            payload["source_policy"] = sourcePolicy;

        var advancedSettings = request.Metadata?.GetProviderOption<object>(GetIdentifier(), "advanced_settings");
        if (advancedSettings is not null)
            payload["advanced_settings"] = advancedSettings;

        var mcpServers = request.Metadata?.GetProviderOption<object>(GetIdentifier(), "mcp_servers");
        if (mcpServers is not null)
            payload["mcp_servers"] = mcpServers;

        var metadata = request.Metadata?.GetProviderOption<Dictionary<string, object?>>(GetIdentifier(), "metadata");
        if (metadata?.Count > 0)
            payload["metadata"] = metadata;

        return payload;
    }

    private object BuildTaskInput(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text!;

        var latestUserText = ExtractLatestUserText(request);
        if (!string.IsNullOrWhiteSpace(latestUserText))
            return latestUserText!;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            parts.Add(request.Instructions!);

        foreach (var item in request.Input?.Items ?? [])
        {
            var text = ExtractItemText(item);
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add($"{item.Role ?? "user"}: {text}");
        }

        return parts.Count == 0 ? string.Empty : string.Join("\n\n", parts);
    }

    private static Dictionary<string, object?>? BuildTaskSpec(AIRequest request)
    {
        if (request.ResponseFormat is null)
            return null;

        var element = request.ResponseFormat is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(request.ResponseFormat, Json);

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            var type = typeEl.GetString();
            if (string.Equals(type, "json_schema", StringComparison.OrdinalIgnoreCase)
                && element.TryGetProperty("json_schema", out var jsonSchema)
                && jsonSchema.ValueKind == JsonValueKind.Object)
            {
                if (jsonSchema.TryGetProperty("schema", out var schema))
                {
                    return new Dictionary<string, object?>
                    {
                        ["output_schema"] = new Dictionary<string, object?>
                        {
                            ["type"] = "json",
                            ["json_schema"] = schema.Clone()
                        }
                    };
                }
            }

            if (string.Equals(type, "json_object", StringComparison.OrdinalIgnoreCase))
            {
                return new Dictionary<string, object?>
                {
                    ["output_schema"] = new Dictionary<string, object?> { ["type"] = "auto" }
                };
            }

            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                return new Dictionary<string, object?>
                {
                    ["output_schema"] = new Dictionary<string, object?> { ["type"] = "text" }
                };
            }
        }

        return null;
    }

    private async IAsyncEnumerable<string> StreamParallelTaskRunEventsRawAsync(
        string runId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{TaskRunsPath}/{Uri.EscapeDataString(runId)}/events");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Parallel task event stream error ({(int)resp.StatusCode}): {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                var data = FlushSseData(dataLines);
                if (!string.IsNullOrWhiteSpace(data) && data is not "[DONE]" and not "[done]")
                    yield return data;
                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line["data:".Length..].TrimStart());
        }

        var trailing = FlushSseData(dataLines);
        if (!string.IsNullOrWhiteSpace(trailing) && trailing is not "[DONE]" and not "[done]")
            yield return trailing;
    }

    private async Task<JsonElement> SendParallelJsonAsync(
        HttpMethod method,
        string uri,
        object? payload = null,
        string operation = "Parallel request",
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, Json),
                Encoding.UTF8,
                MediaTypeNames.Application.Json);
        }

        using var response = await _client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{operation} failed with status {(int)response.StatusCode}: {body}");

        if (string.IsNullOrWhiteSpace(body))
            return JsonSerializer.SerializeToElement(new { }, Json);

        return JsonSerializer.Deserialize<JsonElement>(body, Json).Clone();
    }

    private bool TryFindParallelInteractionId(AIRequest request, out string interactionId)
    {
        interactionId = request.Metadata?.GetProviderOption<string>(GetIdentifier(), "interaction_id")
                        ?? request.Metadata?.GetProviderOption<string>(GetIdentifier(), "interactionId")
                        ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(interactionId))
            return true;

        foreach (var item in request.Input?.Items ?? [])
        {
            foreach (var toolPart in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (toolPart.ProviderExecuted != true)
                    continue;

                if (TryExtractParallelInteraction(toolPart.Output, out interactionId))
                    return true;

                if (TryExtractParallelInteraction(toolPart.Metadata, out interactionId))
                    return true;
            }
        }

        interactionId = string.Empty;
        return false;
    }

    private bool TryExtractParallelInteraction(object? value, out string interactionId)
    {
        interactionId = string.Empty;
        if (value is null)
            return false;

        var element = value is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(value, Json);

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetProperty(element, "structuredContent", out var structuredContent))
            element = structuredContent;

        if (TryGetProperty(element, GetIdentifier(), out var providerScoped) && providerScoped.ValueKind == JsonValueKind.Object)
        {
            if (TryExtractParallelInteraction(providerScoped, out interactionId))
                return true;
        }

        interactionId = TryGetString(element, "interaction_id")
                        ?? TryGetString(element, "interactionId")
                        ?? TryGetString(element, "sessionId")
                        ?? TryGetString(element, "session_id")
                        ?? string.Empty;

        return !string.IsNullOrWhiteSpace(interactionId);
    }

    private AIOutputItem CreateParallelInteractionOutputItem(string interactionId, JsonElement run, AIRequest request)
        => new()
        {
            Type = "tool-call",
            Role = "assistant",
            Content =
            [
                new AIToolCallContentPart
                {
                     Type = "tool-output-available",
                    ToolCallId = BuildParallelInteractionToolCallId(interactionId),
                    ToolName = ParallelInteractionToolName,
                    Title = "Parallel interaction context",
                    Input = new { model = request.Model, processor = NormalizeParallelModel(request.Model) },
                    Output = CreateParallelInteractionToolResult(interactionId, run),
                    ProviderExecuted = true,
                    State = "output-available",
                    Metadata = CreateParallelInteractionMetadata(interactionId, run)
                }
            ],
            Metadata = CreateParallelInteractionMetadata(interactionId, run)
        };

    private IEnumerable<AIStreamEvent> CreateParallelInteractionToolEvents(
        string interactionId,
        JsonElement run,
        AIRequest request,
        DateTimeOffset timestamp)
    {
        var toolCallId = BuildParallelInteractionToolCallId(interactionId);
        var metadata = ToProviderMetadata(GetIdentifier(), CreateParallelInteractionMetadata(interactionId, run));

        yield return CreateParallelStreamEvent(
            GetIdentifier(),
            "tool-input-available",
            toolCallId,
            new AIToolInputAvailableEventData
            {
                ToolName = ParallelInteractionToolName,
                Title = "Parallel interaction context",
                Input = new { model = request.Model, processor = NormalizeParallelModel(request.Model) },
                ProviderExecuted = true,
                ProviderMetadata = metadata
            },
            timestamp);

        yield return CreateParallelStreamEvent(
            GetIdentifier(),
            "tool-output-available",
            toolCallId,
            new AIToolOutputAvailableEventData
            {
                ToolName = ParallelInteractionToolName,
                Output = CreateParallelInteractionToolResult(interactionId, run),
                ProviderExecuted = true,
                ProviderMetadata = metadata
            },
            timestamp);
    }

    private static CallToolResult CreateParallelInteractionToolResult(string interactionId, JsonElement run)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                interaction_id = interactionId,
                interactionId,
                session_id = interactionId,
                sessionId = interactionId,
                run = run.Clone()
            }, Json)
        };

    private Dictionary<string, object?> CreateParallelInteractionMetadata(string interactionId, JsonElement run)
        => new()
        {
            ["type"] = ParallelInteractionToolName,
            ["tool_name"] = ParallelInteractionToolName,
            ["interaction_id"] = interactionId,
            ["interactionId"] = interactionId,
            ["session_id"] = interactionId,
            ["sessionId"] = interactionId,
            ["run"] = run.Clone(),
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                interaction_id = interactionId,
                interactionId,
                session_id = interactionId,
                sessionId = interactionId,
                run = run.Clone()
            }, Json)
        };

    private IEnumerable<AIOutputItem> CreateParallelTaskOutputItems(JsonElement output, JsonElement run)
    {
        if (output.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            yield break;

        var content = ExtractTaskOutputText(output);
        if (!string.IsNullOrWhiteSpace(content))
        {
            yield return new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart {
                    Type = "text",
                     Text = content! }],
                Metadata = CreateParallelTaskOutputMetadata(output, run)
            };
        }

        foreach (var source in CreateParallelSourceOutputItems(output))
            yield return source;
    }

    private IEnumerable<AIOutputItem> CreateParallelSourceOutputItems(JsonElement output)
    {
        if (!TryGetProperty(output, "basis", out var basis) || basis.ValueKind != JsonValueKind.Array)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fieldBasis in basis.EnumerateArray())
        {
            if (!TryGetProperty(fieldBasis, "citations", out var citations) || citations.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var citation in citations.EnumerateArray())
            {
                var url = TryGetString(citation, "url");
                if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
                    continue;

                yield return new AIOutputItem
                {
                    Type = "source-url",
                    Content = [],
                    Metadata = new Dictionary<string, object?>
                    {
                        ["url"] = url,
                        ["title"] = TryGetString(citation, "title"),
                        ["parallel.citation"] = citation.Clone()
                    }
                };
            }
        }
    }

    private IEnumerable<AIStreamEvent> CreateParallelSourceEvents(JsonElement output, DateTimeOffset timestamp)
    {
        if (!TryGetProperty(output, "basis", out var basis) || basis.ValueKind != JsonValueKind.Array)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fieldBasis in basis.EnumerateArray())
        {
            if (!TryGetProperty(fieldBasis, "citations", out var citations) || citations.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var citation in citations.EnumerateArray())
            {
                var url = TryGetString(citation, "url");
                if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
                    continue;

                yield return CreateParallelStreamEvent(
                    GetIdentifier(),
                    "source-url",
                    $"parallel-source-{seen.Count}",
                    new AISourceUrlEventData
                    {
                        SourceId = $"parallel-source-{seen.Count}",
                        Url = url!,
                        Title = TryGetString(citation, "title"),
                        ProviderMetadata = ToProviderMetadata(GetIdentifier(), new Dictionary<string, object?> { ["citation"] = citation.Clone() })
                    },
                    timestamp);
            }
        }
    }

    private Dictionary<string, object?> CreateParallelTaskResponseMetadata(
        string runId,
        string? interactionId,
        JsonElement run,
        JsonElement? output,
        JsonElement? result,
        DateTimeOffset timestamp)
        => new()
        {
            ["parallel.run_id"] = runId,
            ["parallel.interaction_id"] = interactionId,
            ["parallel.run"] = run.Clone(),
            ["parallel.output"] = output?.Clone(),
            ["parallel.result"] = result?.Clone(),
            ["parallel.completed_at"] = timestamp
        };

    private Dictionary<string, object?> CreateParallelTaskOutputMetadata(JsonElement output, JsonElement run)
        => new()
        {
            ["parallel.output"] = output.Clone(),
            ["parallel.run"] = run.Clone(),
            ["parallel.basis"] = TryGetProperty(output, "basis", out var basis) ? basis.Clone() : null
        };

    private Dictionary<string, object>? CreateLooseProviderMetadata(JsonElement raw)
        => new()
        {
            ["raw"] = raw.Clone(),
            ["provider"] = GetIdentifier()
        };

    private static string? ExtractLatestUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = ExtractItemText(item);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static string ExtractItemText(AIInputItem item)
        => string.Join(
            "\n",
            item.Content?.Select(ExtractContentPartText).Where(static text => !string.IsNullOrWhiteSpace(text)) ?? []);

    private static string? ExtractContentPartText(AIContentPart part)
        => part switch
        {
            AITextContentPart text => text.Text,
            AIReasoningContentPart reasoning => reasoning.Text,
            AIToolCallContentPart tool when tool.Output is not null => JsonSerializer.Serialize(tool.Output, Json),
            AIToolCallContentPart tool => JsonSerializer.Serialize(new { tool.ToolName, tool.Input }, Json),
            AIFileContentPart file => file.Filename ?? file.Data?.ToString(),
            _ => null
        };

    private static string? ExtractTaskOutputText(JsonElement output)
    {
        if (output.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        if (TryGetProperty(output, "content", out var content))
        {
            if (TryUnwrapTaskOutputString(content, out var unwrapped))
                return unwrapped;

            return content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Object or JsonValueKind.Array => content.GetRawText(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => content.GetRawText()
            };
        }

        return output.GetRawText();
    }

    private static bool TryUnwrapTaskOutputString(JsonElement content, out string? text)
    {
        text = null;

        if (content.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var propertyName in new[] { "output", "answer", "result", "text" })
        {
            if (!TryGetProperty(content, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
                continue;

            text = value.GetString();
            return !string.IsNullOrWhiteSpace(text);
        }

        return false;
    }

    private static string ToUnifiedTaskStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "completed" => "completed",
            "failed" => "failed",
            "cancelled" => "cancelled",
            "cancelling" or "queued" or "running" or "action_required" => "in_progress",
            _ => "completed"
        };

    private static string ResolveParallelFinishReason(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "failed" => "error",
            "cancelled" or "cancelling" => "cancelled",
            _ => "stop"
        };

    private static string? ExtractParallelErrorText(JsonElement errorEvent)
    {
        if (TryGetProperty(errorEvent, "error", out var error))
            return TryGetString(error, "message") ?? error.GetRawText();

        return TryGetString(errorEvent, "message");
    }

    private static string BuildParallelInteractionToolCallId(string interactionId)
        => $"parallel-interaction-{interactionId}";

    private static string? FlushSseData(List<string> lines)
    {
        if (lines.Count == 0)
            return null;

        var merged = string.Join("\n", lines);
        lines.Clear();

        return string.IsNullOrWhiteSpace(merged) ? null : merged.Trim();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            return true;

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string name)
    {
        var value = TryGetString(element, name);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}
